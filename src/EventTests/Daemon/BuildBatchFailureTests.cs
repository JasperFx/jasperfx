using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using EventTests.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#847 — buildBatchAsync's catch disposed a batch that, on the only path reaching it, had
// never been assigned. The NullReferenceException that raised REPLACED the projection's own
// exception and the `throw` never ran, so every failure during batch construction reported "Object
// reference not set to an instance of an object" and the real cause was gone.
//
// ⚠️ There was no assertion anywhere that a faulted shard reports the projection's OWN exception
// type, which is how a swallow this complete went unnoticed through several releases.
public class BuildBatchFailureTests
{
    private sealed class ProbeException(string message) : Exception(message);

    private sealed class Execution(IEventStore<FakeOperations, FakeSession> store)
        : ProjectionExecution<FakeOperations, FakeSession>(
            ShardName.Compose("probe"),
            new AsyncOptions(),
            store,
            Substitute.For<IEventDatabase>(),
            Substitute.For<IJasperFxProjection<FakeOperations>>(),
            NullLogger.Instance)
    {
        public Task<IProjectionBatch> BuildAsync(EventRange range) => buildBatchAsync(range);
    }

    [Fact]
    public async Task a_failure_before_the_batch_exists_surfaces_its_own_exception()
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>())
            .Returns(_ => throw new ProbeException("the projection's own failure"));

        await using var execution = new Execution(store);

        var ex = await Should.ThrowAsync<ProbeException>(() =>
            execution.BuildAsync(new EventRange(ShardName.Compose("probe"), 10)));

        ex.Message.ShouldBe("the projection's own failure");
    }

    [Fact]
    public async Task the_failure_is_not_masked_by_a_null_reference_from_the_catch()
    {
        // Stated separately from the fact above, because this is the symptom an operator actually
        // saw: the right shard faulted, so nothing looked broken except the diagnosis.
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>())
            .Returns(_ => throw new ProbeException("boom"));

        await using var execution = new Execution(store);

        var ex = await Should.ThrowAsync<ProbeException>(() =>
            execution.BuildAsync(new EventRange(ShardName.Compose("probe"), 10)));

        ex.ShouldNotBeOfType<NullReferenceException>();
    }
}
