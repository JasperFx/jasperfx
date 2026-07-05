using EventTests.Projections;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// Regression coverage for JasperFx/jasperfx#483: a DeleteEvent<T>-driven deletion short-circuits
// ApplyChangesAsync before DetermineActionAsync, so it must record ActionType.Delete and clear the
// slice snapshot itself. Otherwise MarkSliceAction never fans the synthetic ProjectionDeleted<TDoc,TId>
// to downstream composite stages.
public class AggregationRunnerDeleteEventTests
{
    private readonly IAggregationProjection<User, Guid, FakeOperations, FakeSession> theProjection =
        Substitute.For<IAggregationProjection<User, Guid, FakeOperations, FakeSession>>();

    private readonly IProjectionStorage<User, Guid> theStorage = Substitute.For<IProjectionStorage<User, Guid>>();
    private readonly IAggregateCache<Guid, User> theCache = Substitute.For<IAggregateCache<Guid, User>>();
    private readonly IProjectionBatch theBatch = Substitute.For<IProjectionBatch>();

    private AggregationRunner<User, Guid, FakeOperations, FakeSession> theRunner;

    public AggregationRunnerDeleteEventTests()
    {
        // Force the DeleteEvent<T> short-circuit, and stay MultiStream so maybeArchiveStream is a no-op.
        theProjection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(true);
        theProjection.Scope.Returns(AggregationScope.MultiStream);

        theStorage.TenantId.Returns("foo");

        theRunner = new AggregationRunner<User, Guid, FakeOperations, FakeSession>(
            Substitute.For<IEventStore<FakeOperations, FakeSession>>(),
            Substitute.For<IEventDatabase>(),
            theProjection,
            SliceBehavior.None,
            Substitute.For<IEventSlicer>(),
            NullLogger.Instance);
    }

    private async Task<EventSlice<User, Guid>> applyDeleteAsync(User? preExisting)
    {
        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo",
            new IEvent[] { new Event<AEvent>(new AEvent()) })
        {
            Snapshot = preExisting
        };

        await theRunner.ApplyChangesAsync(ShardExecutionMode.Rebuild, theBatch, new FakeOperations(), slice,
            theStorage, theCache, CancellationToken.None);

        return slice;
    }

    [Fact]
    public async Task delete_event_with_pre_existing_snapshot_records_delete_and_clears_snapshot()
    {
        var slice = await applyDeleteAsync(new User("Beast", "Hank McCoy"));

        slice.ResultingAction.ShouldBe(ActionType.Delete);
        slice.Snapshot.ShouldBeNull();
        theStorage.Received().Delete(slice.Id);
    }

    [Fact]
    public async Task delete_event_with_pre_existing_snapshot_fans_projection_deleted_downstream()
    {
        var slice = await applyDeleteAsync(new User("Beast", "Hank McCoy"));

        var range = new EventRange(new ShardName("name"), 0, 100, Substitute.For<ISubscriptionAgent>());
        range.MarkSliceAction("foo", slice);

        var deleted = range.AllRecordedActions().OfType<ProjectionDeleted<User, Guid>>().Single();
        deleted.Identity.ShouldBe(slice.Id);
        deleted.TenantId.ShouldBe("foo");
    }

    [Fact]
    public async Task delete_event_with_no_pre_existing_snapshot_is_a_no_op()
    {
        var slice = await applyDeleteAsync(null);

        // Mirrors buildActionAsync: no prior snapshot means there is nothing to delete, so no
        // synthetic ProjectionDeleted should be fanned to downstream stages.
        slice.ResultingAction.ShouldBe(ActionType.Nothing);
        slice.Snapshot.ShouldBeNull();

        var range = new EventRange(new ShardName("name"), 0, 100, Substitute.For<ISubscriptionAgent>());
        range.MarkSliceAction("foo", slice);

        range.AllRecordedActions().OfType<ProjectionDeleted<User, Guid>>().ShouldBeEmpty();
    }
}
