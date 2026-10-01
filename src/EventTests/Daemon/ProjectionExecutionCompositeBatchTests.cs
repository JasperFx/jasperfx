using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using EventTests.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// A composite member is handed the parent composite's shared batch. The composite executes and disposes
// that batch once every stage has run, so a member must leave it alone. When ProjectionExecution (the
// execution behind EventProjection and other non-aggregation members) disposed it, the batch released its
// session while later stages and the composite's own flush still had operations to write: deferred patch
// operations were silently dropped, and once Block actions stopped running inline the composite faulted
// with an ObjectDisposedException on every batch.
public class ProjectionExecutionCompositeBatchTests
{
    private static (ProjectionExecution<FakeOperations, FakeSession> Execution,
        IEventStore<FakeOperations, FakeSession> Store) buildExecution()
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());

        var execution = new ProjectionExecution<FakeOperations, FakeSession>(
            ShardName.Compose("member"),
            new AsyncOptions(),
            store,
            Substitute.For<IEventDatabase>(),
            Substitute.For<IJasperFxProjection<FakeOperations>>(),
            NullLogger.Instance);

        return (execution, store);
    }

    [Fact]
    public async Task a_composite_member_does_not_dispose_or_execute_the_shared_batch()
    {
        var (execution, _) = buildExecution();
        await using var _execution = execution;

        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        var range = new EventRange(ShardName.Compose("member"), 0, 5, Substitute.For<ISubscriptionAgent>())
        {
            Events = new List<IEvent>(),
            ActiveBatch = batch,
            BatchBehavior = BatchBehavior.Composite
        };

        await execution.ProcessRangeAsync(range);

        await batch.DidNotReceive().DisposeAsync();
        await batch.DidNotReceive().ExecuteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task an_individual_range_executes_and_disposes_its_own_batch()
    {
        var (execution, store) = buildExecution();
        await using var _execution = execution;

        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        store.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch));

        var range = new EventRange(ShardName.Compose("member"), 0, 5, Substitute.For<ISubscriptionAgent>())
        {
            Events = new List<IEvent>()
        };

        range.BatchBehavior.ShouldBe(BatchBehavior.Individual);

        await execution.ProcessRangeAsync(range);

        await batch.Received(1).ExecuteAsync(Arg.Any<CancellationToken>());
        await batch.Received().DisposeAsync();
    }
}
