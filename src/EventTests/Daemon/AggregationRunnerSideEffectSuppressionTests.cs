using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#598/#610: where the blue/green side-effect gate actually withholds side effects now that the
// warm-up runs as ordinary Continuous work instead of as a Rebuild replay.
//
// The gate cannot simply put the execution into Rebuild mode any more. Rebuild mode also switches on the
// deferred-write accumulator (jasperfx#525), whose flush is triggered by the range reaching the AGENT's
// high-water — and inside the warm-up window the agent's loading is clamped to the gate mark, well below
// that, so a deferred window would never flush, committed progression would never advance and the gate
// would never lift. So the runner keeps the real mode for everything it decides for itself, and swaps in
// Rebuild only for the per-slice apply, which is the one place side effects are raised.
public class AggregationRunnerSideEffectSuppressionTests
{
    private readonly IAggregationProjection<User, Guid, FakeOperations, FakeSession> theProjection =
        Substitute.For<IAggregationProjection<User, Guid, FakeOperations, FakeSession>>();

    private readonly IProjectionStorage<User, Guid> theStorage = Substitute.For<IProjectionStorage<User, Guid>>();

    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventSlicer theSlicer = Substitute.For<IEventSlicer>();

    private readonly AsyncOptions theOptions = new();
    private readonly AggregationRunner<User, Guid, FakeOperations, FakeSession> theRunner;

    public AggregationRunnerSideEffectSuppressionTests()
    {
        theProjection.Options.Returns(theOptions);
        theProjection.Scope.Returns(AggregationScope.MultiStream);
        theProjection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(false);

        var snapshot = new User("Beast", "Hank McCoy");
        theProjection
            .DetermineActionAsync(Arg.Any<FakeSession>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>(), Arg.Any<IReadOnlyList<IEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<(User?, ActionType)>((snapshot, ActionType.Store)));
        theProjection
            .TryApplyMetadata(Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>())
            .Returns(((IEvent?)null, snapshot));

        theStorage.TenantId.Returns("foo");

        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        batch.SessionForTenant(Arg.Any<string>()).Returns(new FakeOperations { ProjectionStorage = theStorage });
        theStore
            .StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch));

        theRunner = new AggregationRunner<User, Guid, FakeOperations, FakeSession>(
            theStore,
            Substitute.For<IEventDatabase>(),
            theProjection,
            SliceBehavior.JustInTime,
            theSlicer,
            NullLogger.Instance);
    }

    private async Task buildBatchAsync(bool sideEffectsSuppressed)
    {
        var id = Guid.NewGuid();
        var group = new SliceGroup<User, Guid>("foo");
        group.Slices.Fill(id, new EventSlice<User, Guid>(id, "foo",
            new IEvent[] { new Event<AEvent>(new AEvent()) }));

        theSlicer.SliceAsync(Arg.Any<EventRange>())
            .Returns(new ValueTask<IReadOnlyList<object>>(new object[] { group }));

        var agent = Substitute.For<ISubscriptionAgent>();
        agent.SideEffectsSuppressed.Returns(sideEffectsSuppressed);
        agent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        var range = new EventRange(agent, 0, 100) { BatchBehavior = BatchBehavior.Individual };

        await theRunner.BuildBatchAsync(range, ShardExecutionMode.Continuous, CancellationToken.None);
    }

    [Fact]
    public async Task side_effects_are_raised_for_a_normal_continuous_range()
    {
        await buildBatchAsync(sideEffectsSuppressed: false);

        await theProjection.Received()
            .RaiseSideEffects(Arg.Any<FakeOperations>(), Arg.Any<Guid>(), Arg.Any<IEventSlice<User>>());
    }

    [Fact]
    public async Task side_effects_are_withheld_while_the_agent_is_inside_the_gate_warm_up_window()
    {
        // The point of the whole opt-in: these events were already processed by the PRIOR version of this
        // projection, which already published their messages and appended their raised events.
        await buildBatchAsync(sideEffectsSuppressed: true);

        await theProjection.DidNotReceive()
            .RaiseSideEffects(Arg.Any<FakeOperations>(), Arg.Any<Guid>(), Arg.Any<IEventSlice<User>>());
    }

    [Fact]
    public async Task suppression_does_not_switch_on_the_deferred_rebuild_accumulator()
    {
        // If it did, the flush would wait for a range reaching the agent's high-water — which a clamped
        // warm-up never reaches — so committed progression would stall below the gate mark and the gate
        // would never lift. Aggregate state must still be written per range while suppressed.
        theOptions.RebuildFlushThreshold = 100;

        await buildBatchAsync(sideEffectsSuppressed: true);

        theRunner.DeferredWriteCount.ShouldBe(0);
        theStorage.ReceivedWithAnyArgs().StoreProjection(default!, default, default);
    }
}
