using System.Data.Common;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using EventTests.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#1029: under SkipApplyErrors (the continuous default), a non-transient database error raised from
// projection code -- a missing column, a permissions problem -- must pause the shard, not be dead-lettered.
// Dead-lettering it would fail the next event the same way, and the next, and walk the projection silently
// past the whole event log.
public class SystemicDatabaseFailureSkipTests
{
    private sealed class DriverException(bool transient) : DbException("column \"missing\" does not exist")
    {
        public override bool IsTransient => transient;
    }

    private sealed class Execution(IEventStore<FakeOperations, FakeSession> store, IJasperFxProjection<FakeOperations> projection)
        : ProjectionExecution<FakeOperations, FakeSession>(
            ShardName.Compose("probe"),
            new AsyncOptions(),
            store,
            Substitute.For<IEventDatabase>(),
            projection,
            NullLogger.Instance)
    {
        public Task<IProjectionBatch> BuildAsync(EventRange range) => buildBatchAsync(range);
    }

    private static IEvent anEvent(long sequence)
        => new Event<AEvent>(new AEvent()) { Id = Guid.NewGuid(), Sequence = sequence, StreamId = Guid.NewGuid() };

    private static (Execution Execution, IJasperFxProjection<FakeOperations> Projection, EventRange Range,
        ISubscriptionAgent Agent) arrange(Exception firstFailure)
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>())
            .Returns(new ErrorHandlingOptions { SkipApplyErrors = true });

        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        store.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch));

        var events = new List<IEvent> { anEvent(1), anEvent(2), anEvent(3) };

        // The first build fails on event #2; after a skip, the rebuild succeeds
        var projection = Substitute.For<IJasperFxProjection<FakeOperations>>();
        var calls = 0;
        projection.ApplyAsync(Arg.Any<FakeOperations>(), Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? Task.FromException(new ApplyEventException(events[1], firstFailure)) : Task.CompletedTask);

        var agent = Substitute.For<ISubscriptionAgent>();
        var range = new EventRange(ShardName.Compose("probe"), 0, 3, agent) { Events = events };

        return (new Execution(store, projection), projection, range, agent);
    }

    [Fact]
    public async Task a_systemic_database_failure_is_not_dead_lettered_and_propagates_to_pause_the_shard()
    {
        var (execution, _, range, agent) = arrange(new DriverException(transient: false));
        await using var _ = execution;

        var ex = await Should.ThrowAsync<ApplyEventException>(() => execution.BuildAsync(range));

        ex.InnerException.ShouldBeOfType<DriverException>();
        await agent.DidNotReceive().RecordDeadLetterEventAsync(Arg.Any<DeadLetterEvent>());
        agent.DidNotReceive().MarkSkipped(Arg.Any<long>());
        range.Events.Count.ShouldBe(3);

        // What the supervisor and CritterWatch see: a paused shard that will not restart itself, naming the
        // database's own exception
        var failure = ShardFailure.For(ex, DateTimeOffset.UtcNow);
        failure.IsTransient.ShouldBeFalse();
        failure.RootExceptionType.ShouldContain(nameof(DriverException));
    }

    [Fact]
    public async Task the_projections_own_exception_is_still_dead_lettered()
    {
        var (execution, _, range, agent) = arrange(new InvalidOperationException("poison"));
        await using var _ = execution;

        await execution.BuildAsync(range);

        await agent.Received(1).RecordDeadLetterEventAsync(Arg.Is<DeadLetterEvent>(x => x.EventSequence == 2));
        agent.Received(1).MarkSkipped(2);
        range.Events.Select(x => x.Sequence).ShouldBe([1L, 3L]);
    }
}
