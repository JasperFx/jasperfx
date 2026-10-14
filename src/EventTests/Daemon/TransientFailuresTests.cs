using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Shouldly;

namespace EventTests.Daemon;

// wolverine#4986: a supervisor (Wolverine's node agent, CritterWatch) restarts a paused shard only when the
// failure is expected to clear on its own. ShardFailureCategory.Other cannot say that -- a pool timeout and a
// missing column both come out as Other -- so ShardFailure.IsTransient classifies it from the exception graph.
public class TransientFailuresTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // Stands in for a driver exception: Npgsql, Microsoft.Data.SqlClient and Microsoft.Data.Sqlite all derive
    // from DbException and report IsTransient from the server's error code
    private sealed class DriverException(bool transient, string message = "driver failure") : DbException(message)
    {
        public override bool IsTransient => transient;
    }

    private static Event<AEvent> anEvent(long sequence = 42) => new(new AEvent())
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        StreamId = Guid.NewGuid(),
        EventTypeName = "a_event"
    };

    [Fact]
    public void a_driver_exception_that_reports_itself_transient_is_transient()
    {
        TransientFailures.IsTransient(new DriverException(true)).ShouldBeTrue();
    }

    [Fact]
    public void a_driver_exception_that_does_not_is_not()
    {
        // A missing column, a constraint violation, a syntax error: needs an operator, not a restart
        TransientFailures.IsTransient(new DriverException(false)).ShouldBeFalse();
    }

    [Fact]
    public void timeouts_and_network_failures_are_transient()
    {
        TransientFailures.IsTransient(new TimeoutException()).ShouldBeTrue();
        TransientFailures.IsTransient(new SocketException()).ShouldBeTrue();
        TransientFailures.IsTransient(new IOException("Unable to read data from the transport connection")).ShouldBeTrue();
        TransientFailures.IsTransient(new EndOfStreamException()).ShouldBeTrue();
    }

    [Fact]
    public void file_system_failures_are_not()
    {
        TransientFailures.IsTransient(new FileNotFoundException()).ShouldBeFalse();
        TransientFailures.IsTransient(new DirectoryNotFoundException()).ShouldBeFalse();
    }

    [Fact]
    public void an_ordinary_exception_is_not()
    {
        TransientFailures.IsTransient(new InvalidOperationException()).ShouldBeFalse();
        TransientFailures.IsTransient(new DivideByZeroException()).ShouldBeFalse();
    }

    [Fact]
    public void finds_a_transient_fault_wrapped_in_a_store_exception()
    {
        // The usual shape: a store or daemon exception around the driver's
        var wrapped = new InvalidOperationException("projection batch failed",
            new InvalidOperationException("command failed", new DriverException(true)));

        TransientFailures.IsTransient(wrapped).ShouldBeTrue();
    }

    [Fact]
    public void finds_a_transient_fault_inside_an_aggregate()
    {
        var aggregate = new AggregateException(new DivideByZeroException(), new TimeoutException());

        TransientFailures.IsTransient(aggregate).ShouldBeTrue();
        TransientFailures.IsTransient(new AggregateException(new DivideByZeroException())).ShouldBeFalse();
    }

    [Fact]
    public void the_shard_failure_carries_the_classification()
    {
        // The production case: the pool ran dry during a large rebuild, which pauses the shard as Other
        var poolExhausted = ShardFailure.For(
            new InvalidOperationException("The connection pool has been exhausted", new TimeoutException()), Now);

        poolExhausted.Category.ShouldBe(ShardFailureCategory.Other);
        poolExhausted.IsTransient.ShouldBeTrue();

        var missingColumn = ShardFailure.For(new DriverException(false, "42703: column \"x\" does not exist"), Now);

        missingColumn.Category.ShouldBe(ShardFailureCategory.Other);
        missingColumn.IsTransient.ShouldBeFalse();
    }

    [Fact]
    public void the_classification_is_independent_of_the_category()
    {
        // A timeout in the projection's own lookup is still a single failing event, and still worth a retry
        var failure = ShardFailure.For(new ApplyEventException(anEvent(), new TimeoutException()), Now);

        failure.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        failure.IsTransient.ShouldBeTrue();

        ShardFailure.For(new ApplyEventException(anEvent(), new DivideByZeroException()), Now)
            .IsTransient.ShouldBeFalse();
    }

    private sealed class WeakDriverException() : Exception("a driver that does not implement IsTransient");

    [Fact]
    public void a_store_classifier_can_mark_its_driver_exception_transient()
    {
        TransientFailures.IsTransient(new WeakDriverException()).ShouldBeFalse();

        TransientFailures.AddClassifier(e => e is WeakDriverException ? true : null);

        TransientFailures.IsTransient(new WeakDriverException()).ShouldBeTrue();
        TransientFailures.IsTransient(new InvalidOperationException("wrapped", new WeakDriverException())).ShouldBeTrue();
    }

    private sealed class DeadlineException() : TimeoutException("a timeout this store knows will never clear");

    [Fact]
    public void a_store_classifier_overrides_the_built_in_rules_for_what_it_recognizes()
    {
        TransientFailures.IsTransient(new DeadlineException()).ShouldBeTrue();

        TransientFailures.AddClassifier(e => e is DeadlineException ? false : null);

        TransientFailures.IsTransient(new DeadlineException()).ShouldBeFalse();

        // ...and leaves everything else alone
        TransientFailures.IsTransient(new TimeoutException()).ShouldBeTrue();
    }

    [Fact]
    public void a_failure_serialized_before_the_flag_existed_reads_back_as_not_transient()
    {
        // Conservative on purpose: nothing restarts an old failure automatically
        // A transient failure, written the way it was before the property existed
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(ShardFailure.For(new TimeoutException(), Now)))!.AsObject();
        node.Remove(nameof(ShardFailure.IsTransient)).ShouldBeTrue();

        var failure = JsonSerializer.Deserialize<ShardFailure>(node.ToJsonString())!;

        failure.IsTransient.ShouldBeFalse();
    }

    [Fact]
    public void the_flag_survives_a_round_trip()
    {
        var failure = ShardFailure.For(new TimeoutException(), Now);

        var copy = JsonSerializer.Deserialize<ShardFailure>(JsonSerializer.Serialize(failure))!;

        copy.IsTransient.ShouldBeTrue();
    }
}
