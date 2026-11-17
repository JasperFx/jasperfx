using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#1214: the async apply fast-forwards a slice to its last Compacted<T> marker and keeps only the events
// after it. A stream compacted with nothing appended since leaves no event at all, so the projection's metadata
// step has no last event, and StoreProjection used to get null (Marten threw on it, marten#6036). The marker is
// still the slice's last event, and that is what the store gets now.
public class AggregationRunnerCompactedLastEventTests
{
    private readonly IAggregationProjection<User, Guid, FakeOperations, FakeSession> theProjection =
        Substitute.For<IAggregationProjection<User, Guid, FakeOperations, FakeSession>>();

    private readonly IProjectionStorage<User, Guid> theStorage = Substitute.For<IProjectionStorage<User, Guid>>();

    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventSlicer theSlicer = Substitute.For<IEventSlicer>();

    private readonly AggregationRunner<User, Guid, FakeOperations, FakeSession> theRunner;

    private readonly User theCompactedUser = new("Beast", "Hank McCoy");

    public AggregationRunnerCompactedLastEventTests()
    {
        theProjection.Options.Returns(new AsyncOptions());
        theProjection.Scope.Returns(AggregationScope.SingleStream);
        theProjection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(false);

        // A stream with only its compacted snapshot left still stores that snapshot
        theProjection
            .DetermineActionAsync(Arg.Any<FakeSession>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>(), Arg.Any<IReadOnlyList<IEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(x => new ValueTask<(User?, ActionType)>((x.ArgAt<User?>(1), ActionType.Store)));

        // What the real metadata step returns: the last of the events it was given, null for none
        theProjection
            .TryApplyMetadata(Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>())
            .Returns(x => (x.ArgAt<IReadOnlyList<IEvent>>(0).LastOrDefault(), x.ArgAt<User?>(1)));

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

    private IEvent compactedMarker(long version)
        => new Event<Compacted<User>>(new Compacted<User>(theCompactedUser, Guid.NewGuid(), string.Empty))
        {
            Version = version
        };

    private static IEvent appended(long version) => new Event<AEvent>(new AEvent()) { Version = version };

    private async Task applyAsync(params IEvent[] events)
    {
        var id = Guid.NewGuid();
        var group = new SliceGroup<User, Guid>("foo");
        group.Slices.Fill(id, new EventSlice<User, Guid>(id, "foo", events));

        theSlicer.SliceAsync(Arg.Any<EventRange>())
            .Returns(new ValueTask<IReadOnlyList<object>>(new object[] { group }));

        var agent = Substitute.For<ISubscriptionAgent>();
        agent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        var range = new EventRange(agent, 0, 100) { BatchBehavior = BatchBehavior.Individual };

        await theRunner.BuildBatchAsync(range, ShardExecutionMode.Continuous, CancellationToken.None);
    }

    [Fact]
    public async Task a_slice_ending_with_its_compacted_marker_stores_the_snapshot_with_the_marker_as_last_event()
    {
        var marker = compactedMarker(10);

        await applyAsync(appended(9), marker);

        theStorage.Received(1).StoreProjection(theCompactedUser, marker, AggregationScope.SingleStream);
        theStorage.DidNotReceive().StoreProjection(Arg.Any<User>(), null, Arg.Any<AggregationScope>());
    }

    [Fact]
    public async Task events_after_the_compacted_marker_still_supply_the_last_event()
    {
        var last = appended(12);

        await applyAsync(compactedMarker(10), appended(11), last);

        theStorage.Received(1).StoreProjection(theCompactedUser, last, AggregationScope.SingleStream);
    }

    [Fact]
    public void fast_forwarding_a_slice_returns_the_marker_and_keeps_only_the_events_after_it()
    {
        var marker = compactedMarker(10);
        var after = appended(11);
        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo", [appended(9), marker, after]);

        slice.FastForwardForCompacting().ShouldBeSameAs(marker);

        slice.Snapshot.ShouldBe(theCompactedUser);
        slice.Events().ShouldBe([after]);
    }

    [Fact]
    public void fast_forwarding_a_slice_without_a_marker_changes_nothing()
    {
        var events = new[] { appended(1), appended(2) };
        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo", events);

        slice.FastForwardForCompacting().ShouldBeNull();

        slice.Snapshot.ShouldBeNull();
        slice.Events().ShouldBe(events);
    }
}
