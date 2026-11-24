using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections.Composite;

/// <summary>
/// jasperfx#684: with KeepUnchangedMembers on, a member whose own progression row is ahead of the composite's
/// position skips the composite's replay up to that row, and a rebuild leaves it standing. Off, nothing changes.
/// </summary>
public class CompositeKeepUnchangedMembersTests
{
    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly ISubscriptionAgent theAgent = Substitute.For<ISubscriptionAgent>();
    private readonly ShardName theShardName = new("Composite", ShardName.All, 1);
    private readonly FakeProgressionTable theProgression = new();

    // What each member was handed, by member
    private readonly Dictionary<string, List<EventRange>> theProcessedRanges = new();

    private readonly ISubscriptionExecution theKeptMember;
    private readonly ISubscriptionExecution theChangedMember;

    public CompositeKeepUnchangedMembersTests()
    {
        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        batch.RecordProgress(Arg.Any<EventRange>()).Returns(call =>
        {
            theProgression.Record(call.Arg<EventRange>());
            return ValueTask.CompletedTask;
        });

        theStore.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                theProgression.Record(call.Arg<EventRange>());
                return new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch);
            });
        theStore.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>())
            .Returns(new ErrorHandlingOptions { SkipApplyErrors = false });

        theDatabase.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
            .Returns(call => theProgression.PositionOf(call.Arg<ShardName>().Identity) ?? 0);

        theKeptMember = member("Kept");
        theChangedMember = member("Changed");

        theAgent.Name.Returns(theShardName);
        theAgent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());
        theAgent.Status.Returns(AgentStatus.Running);
    }

    private ISubscriptionExecution member(string name)
    {
        var execution = Substitute.For<ISubscriptionExecution>();
        execution.ShardName.Returns(new ShardName(name, ShardName.All, 1));
        execution.CompactCachesAsync().Returns(Task.CompletedTask);
        theProcessedRanges[name] = [];
        execution.ProcessRangeAsync(Arg.Any<EventRange>()).Returns(call =>
        {
            lock (theProcessedRanges) theProcessedRanges[name].Add(call.Arg<EventRange>());
            return Task.CompletedTask;
        });
        return execution;
    }

    private CompositeExecution<FakeOperations, FakeSession> execution(bool keep)
    {
        return new CompositeExecution<FakeOperations, FakeSession>(theShardName, new AsyncOptions(), theStore,
            theDatabase, Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance,
            [new ExecutionStage([theKeptMember, theChangedMember])])
        {
            KeepsUnchangedMembers = keep,
            Mode = ShardExecutionMode.CatchUp
        };
    }

    private async Task processAsync(CompositeExecution<FakeOperations, FakeSession> composite, long floor, long ceiling)
    {
        var events = new List<IEvent>();
        for (var sequence = floor + 1; sequence <= ceiling; sequence++)
        {
            events.Add(new Event<AEvent>(new AEvent()) { Sequence = sequence });
        }

        await composite.ProcessRangeAsync(new EventRange(theAgent, floor, ceiling) { Events = events });
        await theAgent.DidNotReceive().ReportCriticalFailureAsync(Arg.Any<Exception>());
    }

    [Fact]
    public async Task a_member_ahead_of_the_composite_skips_the_replay_up_to_its_own_row()
    {
        // The kept member ran to 10 under its current version. The composite was bumped, so its row is new.
        theProgression.Seed("Kept:All", 10);

        var composite = execution(keep: true);
        await processAsync(composite, 0, 5);
        await processAsync(composite, 5, 10);
        await processAsync(composite, 10, 15);

        // The kept member did nothing until the replay passed its row, then picked up from there
        var kept = theProcessedRanges["Kept"].ShouldHaveSingleItem();
        kept.SequenceFloor.ShouldBe(10);
        kept.Events.Select(x => x.Sequence).ShouldBe([11L, 12L, 13L, 14L, 15L]);

        // The changed member replayed everything
        theProcessedRanges["Changed"].Select(x => x.SequenceCeiling).ShouldBe([5L, 10L, 15L]);

        theProgression.PositionOf("Kept:All").ShouldBe(15);
        theProgression.PositionOf("Changed:All").ShouldBe(15);
        theProgression.PositionOf("Composite:All").ShouldBe(15);
    }

    [Fact]
    public async Task a_range_that_straddles_the_kept_row_hands_the_member_only_the_events_after_it()
    {
        theProgression.Seed("Kept:All", 7);

        await processAsync(execution(keep: true), 0, 10);

        var kept = theProcessedRanges["Kept"].ShouldHaveSingleItem();
        kept.SequenceFloor.ShouldBe(7);
        kept.SequenceCeiling.ShouldBe(10);
        kept.Events.Select(x => x.Sequence).ShouldBe([8L, 9L, 10L]);

        theProcessedRanges["Changed"].ShouldHaveSingleItem().Events.Count.ShouldBe(10);

        // Its progress write moved its own row on from 7, rather than inserting a second one from 0
        theProgression.PositionOf("Kept:All").ShouldBe(10);
    }

    [Fact]
    public async Task members_in_lockstep_with_the_composite_are_not_kept()
    {
        theProgression.Seed("Composite:All", 10);
        theProgression.Seed("Kept:All", 10);
        theProgression.Seed("Changed:All", 10);

        await processAsync(execution(keep: true), 10, 15);

        theProcessedRanges["Kept"].ShouldHaveSingleItem().SequenceFloor.ShouldBe(10);
        theProcessedRanges["Changed"].ShouldHaveSingleItem().SequenceFloor.ShouldBe(10);
    }

    [Fact]
    public async Task off_by_default_every_member_replays_and_no_member_row_is_read()
    {
        var composite = new CompositeExecution<FakeOperations, FakeSession>(theShardName, new AsyncOptions(),
            theStore, theDatabase, Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance,
            [new ExecutionStage([theKeptMember, theChangedMember])]) { Mode = ShardExecutionMode.CatchUp };

        await processAsync(composite, 0, 5);

        theProcessedRanges["Kept"].ShouldHaveSingleItem().SequenceFloor.ShouldBe(0);
        theProcessedRanges["Changed"].ShouldHaveSingleItem().SequenceFloor.ShouldBe(0);
        await theDatabase.DidNotReceive().ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void the_composite_hands_its_executions_the_setting()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite") { KeepUnchangedMembers = true };
        composite.StageFor(1).Add(new FakeReplayMember("A", canParticipate: true));

        var built = (CompositeExecution<FakeOperations, FakeSession>)((ISubscriptionFactory<FakeOperations, FakeSession>)composite)
            .BuildExecution(theStore, theDatabase, NullLogger.Instance, theShardName);

        built.KeepsUnchangedMembers.ShouldBeTrue();
    }

    [Fact]
    public void a_multi_stage_composite_does_not_keep_members()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite") { KeepUnchangedMembers = true };
        composite.StageFor(1).Add(new FakeReplayMember("A", canParticipate: true));
        composite.StageFor(2).Add(new FakeReplayMember("B", canParticipate: true));

        composite.KeepsUnchangedMembers.ShouldBeFalse();

        var built = (CompositeExecution<FakeOperations, FakeSession>)((ISubscriptionFactory<FakeOperations, FakeSession>)composite)
            .BuildExecution(theStore, theDatabase, NullLogger.Instance, theShardName);
        built.KeepsUnchangedMembers.ShouldBeFalse();
    }

    [Fact]
    public async Task off_by_default_a_rebuild_tears_down_every_member()
    {
        var composite = compositeOf("A", "B");
        composite.KeepUnchangedMembers = false;
        theProgression.Seed("A:All", 10);

        (await composite.MembersToTearDownAsync(theDatabase)).Select(x => x.Name).ShouldBe(["A", "B"]);
        await theDatabase.DidNotReceive().ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task after_a_bump_a_rebuild_leaves_the_members_ahead_of_the_composite_standing()
    {
        var composite = compositeOf("A", "B");

        // A kept its version and ran to 10; B was bumped, so its current version has no row; the composite was
        // bumped too, so its current version has no row either
        theProgression.Seed("A:All", 10);

        (await composite.MembersToTearDownAsync(theDatabase)).Select(x => x.Name).ShouldBe(["B"]);
    }

    [Fact]
    public async Task a_rebuild_with_no_bump_tears_down_every_member()
    {
        var composite = compositeOf("A", "B");

        // Everything in lockstep: a rebuild to repair data, not to pick up a new version
        theProgression.Seed("Composite:All", 10);
        theProgression.Seed("A:All", 10);
        theProgression.Seed("B:All", 10);

        (await composite.MembersToTearDownAsync(theDatabase)).Select(x => x.Name).ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task a_multi_stage_composite_tears_down_every_member()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite") { KeepUnchangedMembers = true };
        composite.StageFor(1).Add(new FakeReplayMember("A", canParticipate: true));
        composite.StageFor(2).Add(new FakeReplayMember("B", canParticipate: true));
        theProgression.Seed("A:All", 10);

        (await composite.MembersToTearDownAsync(theDatabase)).Select(x => x.Name).ShouldBe(["A", "B"]);
    }

    private static CompositeProjection<FakeOperations, FakeSession> compositeOf(params string[] members)
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite") { KeepUnchangedMembers = true };
        foreach (var name in members)
        {
            composite.StageFor(1).Add(new FakeReplayMember(name, canParticipate: true));
        }

        return composite;
    }

    // Mirrors Marten's progression writes: a range from floor 0 inserts the row, any other range updates it only
    // where it is still at that floor.
    private class FakeProgressionTable
    {
        private readonly Dictionary<string, long> _rows = new();

        public void Seed(string identity, long position) => _rows[identity] = position;

        public void Record(EventRange range)
        {
            lock (_rows)
            {
                var name = range.ShardName.Identity;
                if (range.SequenceFloor == 0)
                {
                    _rows.Add(name, range.SequenceCeiling);
                    return;
                }

                if (!_rows.TryGetValue(name, out var current) || current != range.SequenceFloor)
                {
                    throw new ProgressionProgressOutOfOrderException(name, range.SequenceFloor, range.SequenceCeiling);
                }

                _rows[name] = range.SequenceCeiling;
            }
        }

        public long? PositionOf(string identity)
        {
            lock (_rows) return _rows.TryGetValue(identity, out var position) ? position : null;
        }
    }
}
