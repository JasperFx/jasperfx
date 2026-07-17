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

// jasperfx#525: the deferred single-flush rebuild accumulator. During a rebuild with RebuildFlushThreshold > 0,
// AggregationRunner buffers the latest snapshot (or tombstone) per aggregate id instead of writing per page, and
// emits exactly one operation per aggregate when it flushes at the threshold or the rebuild ceiling. These tests
// drive ApplyChangesAsync + the flush directly (the pattern AggregationRunnerDeleteEventTests established). The
// multi-page read-through invariant is covered end-to-end by Marten's integration harness.
public class AggregationRunnerDeferredRebuildTests
{
    private readonly IAggregationProjection<User, Guid, FakeOperations, FakeSession> theProjection =
        Substitute.For<IAggregationProjection<User, Guid, FakeOperations, FakeSession>>();

    private readonly IProjectionStorage<User, Guid> theStorage = Substitute.For<IProjectionStorage<User, Guid>>();
    private readonly IAggregateCache<Guid, User> theCache = Substitute.For<IAggregateCache<Guid, User>>();
    private readonly IProjectionBatch theBatch = Substitute.For<IProjectionBatch>();
    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly AsyncOptions theOptions = new() { RebuildFlushThreshold = 100 };
    private readonly AggregationRunner<User, Guid, FakeOperations, FakeSession> theRunner;

    public AggregationRunnerDeferredRebuildTests()
    {
        theProjection.Options.Returns(theOptions);
        theProjection.Scope.Returns(AggregationScope.MultiStream);
        theStorage.TenantId.Returns("foo");

        theRunner = new AggregationRunner<User, Guid, FakeOperations, FakeSession>(
            theStore,
            Substitute.For<IEventDatabase>(),
            theProjection,
            SliceBehavior.None,
            Substitute.For<IEventSlicer>(),
            NullLogger.Instance);
    }

    // Configure the projection so a slice resolves to ActionType.Store yielding the given snapshot.
    private void storeYields(User snapshot)
    {
        theProjection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(false);
        theProjection
            .DetermineActionAsync(Arg.Any<FakeSession>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>(), Arg.Any<IReadOnlyList<IEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<(User?, ActionType)>((snapshot, ActionType.Store)));
        theProjection
            .TryApplyMetadata(Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>())
            .Returns(((IEvent?)null, snapshot));
    }

    private async Task applyStoreAsync(Guid id, User snapshot)
    {
        storeYields(snapshot);
        var slice = new EventSlice<User, Guid>(id, "foo", new IEvent[] { new Event<AEvent>(new AEvent()) });
        await theRunner.ApplyChangesAsync(ShardExecutionMode.Rebuild, theBatch, new FakeOperations(), slice,
            theStorage, theCache, CancellationToken.None);
    }

    // Drive the DeleteEvent<T> short-circuit: a matching delete event plus a pre-existing snapshot (stream
    // ownership) records a delete tombstone into the accumulator.
    private async Task applyDeleteAsync(Guid id, User preExisting)
    {
        theProjection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(true);
        var slice = new EventSlice<User, Guid>(id, "foo", new IEvent[] { new Event<AEvent>(new AEvent()) })
        {
            Snapshot = preExisting
        };
        await theRunner.ApplyChangesAsync(ShardExecutionMode.Rebuild, theBatch, new FakeOperations(), slice,
            theStorage, theCache, CancellationToken.None);
    }

    private EventRange flushRange(long ceiling, long highWaterMark = 0)
    {
        var agent = Substitute.For<ISubscriptionAgent>();
        agent.HighWaterMark.Returns(highWaterMark);
        agent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());
        return new EventRange(agent, 0, ceiling);
    }

    // Wire theStore.StartProjectionBatchAsync -> a substitute batch whose per-tenant session hands back theStorage,
    // so the flush's writes land on the storage substitute we assert against.
    private IProjectionBatch<FakeOperations, FakeSession> wireFlushBatch()
    {
        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        var operations = new FakeOperations { ProjectionStorage = theStorage };
        batch.SessionForTenant("foo").Returns(operations);
        theStore
            .StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                ShardExecutionMode.Rebuild, Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch));
        return batch;
    }

    [Fact]
    public void defers_only_for_rebuild_individual_batches_with_a_positive_threshold()
    {
        var individual = new EventRange(Substitute.For<ISubscriptionAgent>(), 0, 10)
            { BatchBehavior = BatchBehavior.Individual };
        var composite = new EventRange(Substitute.For<ISubscriptionAgent>(), 0, 10)
            { BatchBehavior = BatchBehavior.Composite };

        theRunner.DefersRebuildWrites(ShardExecutionMode.Rebuild, individual).ShouldBeTrue();

        // Off in every other mode, for composite batches, and when the threshold is 0.
        theRunner.DefersRebuildWrites(ShardExecutionMode.Continuous, individual).ShouldBeFalse();
        theRunner.DefersRebuildWrites(ShardExecutionMode.CatchUp, individual).ShouldBeFalse();
        theRunner.DefersRebuildWrites(ShardExecutionMode.Rebuild, composite).ShouldBeFalse();

        theOptions.RebuildFlushThreshold = 0;
        theRunner.DefersRebuildWrites(ShardExecutionMode.Rebuild, individual).ShouldBeFalse();
    }

    [Fact]
    public async Task accumulates_without_writing_until_flush()
    {
        theRunner.BeginDeferredRebuildWindowForTesting();

        var id = Guid.NewGuid();
        await applyStoreAsync(id, new User("Beast", "Hank"));
        await applyStoreAsync(id, new User("Beast", "Hank McCoy")); // same id, later page

        // Nothing hit the database during accumulation, and the two writes for one id collapse to one dirty entry.
        theStorage.DidNotReceive().StoreProjection(Arg.Any<User>(), Arg.Any<IEvent?>(), Arg.Any<AggregationScope>());
        theStorage.DidNotReceiveWithAnyArgs()
            .StoreProjectionForRebuildFlush(default!, default, default, default);
        theRunner.DeferredWriteCount.ShouldBe(1);

        wireFlushBatch();
        var range = flushRange(50);
        await theRunner.FlushDeferredRebuildWritesAsync(range, 50, CancellationToken.None);

        // Exactly one op per aggregate per flush window, routed as a first-time (not previously flushed) write,
        // carrying the LATEST snapshot; progress advances to the flush ceiling.
        theStorage.Received(1).StoreProjectionForRebuildFlush(
            Arg.Is<User>(u => u.RealName == "Hank McCoy"), Arg.Any<IEvent?>(), AggregationScope.MultiStream, false);
        await range.Agent.Received().MarkSuccessAsync(50);
        theRunner.DeferredWriteCount.ShouldBe(0);
    }

    [Fact]
    public async Task one_op_per_distinct_aggregate()
    {
        theRunner.BeginDeferredRebuildWindowForTesting();

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await applyStoreAsync(a, new User("A", "A"));
        await applyStoreAsync(b, new User("B", "B"));
        await applyStoreAsync(a, new User("A", "A2")); // a touched again

        theRunner.DeferredWriteCount.ShouldBe(2);

        wireFlushBatch();
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(99), 99, CancellationToken.None);

        theStorage.Received(1).StoreProjectionForRebuildFlush(
            Arg.Is<User>(u => u.RealName == "A2"), Arg.Any<IEvent?>(), Arg.Any<AggregationScope>(), false);
        theStorage.Received(1).StoreProjectionForRebuildFlush(
            Arg.Is<User>(u => u.RealName == "B"), Arg.Any<IEvent?>(), Arg.Any<AggregationScope>(), false);
    }

    [Fact]
    public async Task overflow_reflush_routes_previously_flushed_ids_as_upserts()
    {
        theRunner.BeginDeferredRebuildWindowForTesting();
        wireFlushBatch();

        var id = Guid.NewGuid();

        // Window 1: first flush -> first-time write (previouslyFlushed == false).
        await applyStoreAsync(id, new User("v", "1"));
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(10), 10, CancellationToken.None);

        // Window 2: same id reappears (overflow) -> must be routed as an UPSERT (previouslyFlushed == true).
        await applyStoreAsync(id, new User("v", "2"));
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(20), 20, CancellationToken.None);

        theStorage.Received(1).StoreProjectionForRebuildFlush(
            Arg.Is<User>(u => u.RealName == "1"), Arg.Any<IEvent?>(), Arg.Any<AggregationScope>(), false);
        theStorage.Received(1).StoreProjectionForRebuildFlush(
            Arg.Is<User>(u => u.RealName == "2"), Arg.Any<IEvent?>(), Arg.Any<AggregationScope>(), true);
    }

    [Fact]
    public async Task empty_final_flush_still_advances_progress()
    {
        wireFlushBatch();
        theRunner.BeginDeferredRebuildWindowForTesting();

        // No pending writes, but the flush must still run a batch (to advance the DB progression to the ceiling)
        // and mark success so the rebuild completes. No document writes should be emitted.
        var range = flushRange(200);
        await theRunner.FlushDeferredRebuildWritesAsync(range, 200, CancellationToken.None);

        await range.Agent.Received().MarkSuccessAsync(200);
        theStorage.DidNotReceiveWithAnyArgs()
            .StoreProjectionForRebuildFlush(default!, default, default, default);
    }

    [Fact]
    public async Task deferred_delete_is_buffered_then_emitted_once_at_flush()
    {
        theProjection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(true);
        theRunner.BeginDeferredRebuildWindowForTesting();

        var id = Guid.NewGuid();
        var slice = new EventSlice<User, Guid>(id, "foo", new IEvent[] { new Event<AEvent>(new AEvent()) })
        {
            Snapshot = new User("Beast", "Hank") // pre-existing => owns stream => a real delete
        };
        await theRunner.ApplyChangesAsync(ShardExecutionMode.Rebuild, theBatch, new FakeOperations(), slice,
            theStorage, theCache, CancellationToken.None);

        theStorage.DidNotReceive().Delete(Arg.Any<Guid>());
        theRunner.DeferredWriteCount.ShouldBe(1);

        wireFlushBatch();
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(30), 30, CancellationToken.None);

        theStorage.Received(1).Delete(id);
    }

    [Fact]
    public async Task delete_overwrites_a_pending_store_in_the_same_window_and_flushes_as_a_delete()
    {
        theRunner.BeginDeferredRebuildWindowForTesting();

        var id = Guid.NewGuid();
        await applyStoreAsync(id, new User("Beast", "Hank"));   // pending store
        await applyDeleteAsync(id, new User("Beast", "Hank"));  // ...superseded by a delete in the same window

        theRunner.DeferredWriteCount.ShouldBe(1); // one entry for the id, now a tombstone

        wireFlushBatch();
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(40), 40, CancellationToken.None);

        // The delete wins: a Delete is emitted, and NO store op for that id.
        theStorage.Received(1).Delete(id);
        theStorage.DidNotReceiveWithAnyArgs()
            .StoreProjectionForRebuildFlush(default!, default, default, default);
    }

    [Fact]
    public async Task deleting_a_flushed_id_lets_a_later_recreate_be_a_fresh_insert()
    {
        wireFlushBatch();
        theRunner.BeginDeferredRebuildWindowForTesting();
        var id = Guid.NewGuid();

        // Window 1: create + flush -> first-time INSERT.
        await applyStoreAsync(id, new User("v", "1"));
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(10), 10, CancellationToken.None);

        // Window 2: delete + flush -> row removed, id dropped from the flushed set.
        await applyDeleteAsync(id, new User("v", "1"));
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(20), 20, CancellationToken.None);

        // Window 3: recreate + flush -> because the row was deleted, this must be a FRESH insert, not an UPSERT.
        await applyStoreAsync(id, new User("v", "3"));
        await theRunner.FlushDeferredRebuildWritesAsync(flushRange(30), 30, CancellationToken.None);

        theStorage.Received(1).Delete(id);
        theStorage.Received(1).StoreProjectionForRebuildFlush(
            Arg.Is<User>(u => u.RealName == "3"), Arg.Any<IEvent?>(), Arg.Any<AggregationScope>(), false);
    }

    [Fact]
    public async Task discard_drops_the_accumulator_without_writing()
    {
        theRunner.BeginDeferredRebuildWindowForTesting();
        await applyStoreAsync(Guid.NewGuid(), new User("A", "A"));
        theRunner.DeferredWriteCount.ShouldBe(1);

        theRunner.DiscardDeferredRebuildWrites();

        theRunner.DeferredWriteCount.ShouldBe(0);
        theStorage.DidNotReceiveWithAnyArgs()
            .StoreProjectionForRebuildFlush(default!, default, default, default);
    }

    [Fact]
    public async Task flush_due_at_threshold_or_ceiling()
    {
        theOptions.RebuildFlushThreshold = 2;
        theRunner.BeginDeferredRebuildWindowForTesting();

        // One dirty aggregate, ceiling not yet reached => not due.
        await applyStoreAsync(Guid.NewGuid(), new User("A", "A"));
        theRunner.DeferredFlushDue(flushRange(10, highWaterMark: 100)).ShouldBeFalse();

        // Threshold reached => due.
        await applyStoreAsync(Guid.NewGuid(), new User("B", "B"));
        theRunner.DeferredFlushDue(flushRange(10, highWaterMark: 100)).ShouldBeTrue();

        // Final range (ceiling reaches the high-water target) is always due, even below threshold.
        theRunner.DiscardDeferredRebuildWrites();
        theRunner.BeginDeferredRebuildWindowForTesting();
        theRunner.DeferredFlushDue(flushRange(100, highWaterMark: 100)).ShouldBeTrue();
    }
}
