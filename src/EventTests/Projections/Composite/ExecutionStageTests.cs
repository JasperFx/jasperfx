using System.Collections.Concurrent;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections.Composite;

// #4751: a composite's member stages must record progress and accumulate their read-model writes into
// the SAME ProjectionBatch as the parent composite, so the members' progression + writes commit
// atomically together with the composite shard's own progression in a single batch.ExecuteAsync().
// If a member ever ran against its own batch, the composite shard's progression could commit ahead of
// the member read models, which is the premature "non-stale" hazard behind #4751. ExecutionStage.
// ExecuteDownstreamAsync now asserts the shared-batch invariant; this pins it.
public class ExecutionStageTests
{
    [Fact]
    public async Task members_record_and_process_against_the_shared_composite_batch()
    {
        var agent = Substitute.For<ISubscriptionAgent>();
        var batch = Substitute.For<IProjectionBatch>();

        var parent = new EventRange(ShardName.Compose("Cmp"), 0, 5, agent)
        {
            Events = new List<IEvent>(),
            ActiveBatch = batch
        };

        var seenBatches = new ConcurrentBag<IProjectionBatch?>();

        ISubscriptionExecution member(string name)
        {
            var execution = Substitute.For<ISubscriptionExecution>();
            execution.ShardName.Returns(ShardName.Compose(name));
            execution.ProcessRangeAsync(Arg.Any<EventRange>()).Returns(ci =>
            {
                seenBatches.Add(ci.Arg<EventRange>().ActiveBatch);
                return Task.CompletedTask;
            });
            return execution;
        }

        var stage = new ExecutionStage([member("CmpTrip"), member("CmpTripCount")]);

        // Must NOT throw the #4751 shared-batch guard.
        await stage.ExecuteDownstreamAsync(parent);

        // Every member processed against the SAME composite batch instance, and each recorded its
        // progress into that batch — so progression and writes are committed together, never split.
        seenBatches.Count.ShouldBe(2);
        seenBatches.ShouldAllBe(b => ReferenceEquals(b, batch));
        await batch.Received(2).RecordProgress(Arg.Any<EventRange>());
    }
}
