using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ImTools;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public class AggregationRunner<TDoc, TId, TOperations, TQuerySession> : IGroupedProjectionRunner, IAggregateCaching<TId, TDoc>
    where TOperations : TQuerySession, IStorageOperations where TId : notnull where TDoc : notnull
{
    private readonly object _cacheLock = new();
    private readonly IEventDatabase _database;
    private readonly ILogger _logger;
    private readonly IEventStore<TOperations, TQuerySession> _store;
    private ImHashMap<string, IAggregateCache<TId, TDoc>> _caches = ImHashMap<string, IAggregateCache<TId, TDoc>>.Empty;

    // #4730: cache mutations from the current build, applied to the cache only AFTER the batch
    // commits (ApplyPendingCacheUpdates). Reassigned per build, so a failed/uncommitted build
    // leaves no mutations behind for a retry to read. Composite batches bypass this and write the
    // cache during build (see _populateCacheImmediately) because downstream stages read upstream
    // in-flight aggregates from the cache within the same batch (marten#4329).
    private ConcurrentQueue<(IAggregateCache<TId, TDoc> Cache, TId Id, TDoc? Snapshot, bool Remove)> _pendingCacheUpdates = new();
    private bool _populateCacheImmediately;

    // jasperfx#525: the deferred-rebuild write accumulator. Unlike _pendingCacheUpdates (reset per batch),
    // this persists across ranges for the whole rebuild until a flush commits or a stop discards it, so a
    // stream spanning several pages is written exactly once per flush window instead of once per page. Guarded
    // by _bufferLock because slices within a range are applied on a parallel Block. Non-null only while a
    // deferred rebuild is active (RebuildFlushThreshold > 0, Rebuild mode, Individual batch).
    private readonly object _bufferLock = new();
    private RebuildFlushWindow? _flush;
    private bool _deferWrites;

    private int rebuildFlushThreshold => Projection.Options.RebuildFlushThreshold;

    public AggregationRunner(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        IAggregationProjection<TDoc, TId, TOperations, TQuerySession> projection,
        SliceBehavior sliceBehavior, IEventSlicer slicer, ILogger logger)
    {
        Projection = projection;
        SliceBehavior = sliceBehavior;
        _store = store;
        _database = database;
        _logger = logger;
        Slicer = slicer;
    }

    public IAggregationProjection<TDoc, TId, TOperations, TQuerySession> Projection { get; }

    public IEventSlicer Slicer { get; }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public SliceBehavior SliceBehavior { get; }

    public async Task<IProjectionBatch> BuildBatchAsync(EventRange range, ShardExecutionMode mode,
        CancellationToken cancellation)
    {
        Projection.StartBatch();

        // #4730: discard any cache mutations from a prior build that did not commit (e.g. a build
        // that threw, or a skip-and-rebuild attempt) before accumulating this build's mutations.
        _pendingCacheUpdates = new();

        // Composite member batches must populate the cache during build so a downstream stage can
        // read an upstream stage's in-flight aggregate (marten#4329). Standalone (Individual)
        // batches defer cache population until the batch commits.
        _populateCacheImmediately = range.BatchBehavior == BatchBehavior.Composite;

        // jasperfx#525: for a threshold-enabled Individual rebuild batch, the per-slice writes below are
        // buffered instead of executed, and the (empty) batch built here is used only to load prior snapshots.
        // Open the accumulator once and keep it across ranges until a flush or discard.
        _deferWrites = DefersRebuildWrites(mode, range);
        if (_deferWrites)
        {
            lock (_bufferLock)
            {
                _flush ??= new RebuildFlushWindow();
                // Seed the flush window's floor from the first range so the flush's progression update keys off
                // the projection's current committed progression.
                _flush.EnsureFloor(range.SequenceFloor);
            }
        }

        var batch = range.ActiveBatch as IProjectionBatch<TOperations, TQuerySession> ?? await _store.StartProjectionBatchAsync(range, _database, mode, Projection.Options, cancellation);

        if (SliceBehavior == SliceBehavior.JustInTime)
        {
            // TODO -- instrument this maybe?
            // This will need to pass in the database somehow for slicers that use a Marten database
            await range.SliceAsync(Slicer);
        }

        var exceptions = new List<Exception>();
        var builder = new Block<EventSliceExecution>(10, async (execution, _) =>
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            await ApplyChangesAsync(mode, batch, execution.Operations, execution.Slice, execution.Storage,
                execution.Cache, cancellation);
        });

        builder.OnError = (_, e) => exceptions.Add(e);

        var groups = range.Groups.OfType<SliceGroup<TDoc, TId>>().ToArray();
        foreach (var group in groups)
        {
            await processBatchAsync(cancellation, batch, group, builder);
        }

        await builder.WaitForCompletionAsync().ConfigureAwait(false);

        foreach (var group in groups)
        {
            foreach (var slice in group.Slices)
            {
                range.MarkSliceAction(group.TenantId, slice);
            }
        }

        if (exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Throw(exceptions[0]);
        }
        else if (exceptions.Any())
        {
            throw new AggregateException(exceptions);
        }

        // #4730: for standalone (Individual) batches, both cache population and compaction are
        // deferred to ApplyPendingCacheUpdates, which the execution calls only after the batch
        // commits. Composite member batches populated the cache during build above and defer
        // compaction until every stage has run (CompositeExecution calls CompactCachesAsync after
        // all stages complete) so a downstream stage can read the upstream stage's in-flight
        // entities without them being evicted out from under it. See JasperFx/marten#4329.

        await Projection.EndBatchAsync();

        return batch;
    }

    public Task CompactCachesAsync()
    {
        try
        {
            foreach (var pair in _caches.Enumerate())
            {
                pair.Value.CompactIfNecessary();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to compact aggregate caches for {ProjectionName}", Projection.Name);
        }

        return Task.CompletedTask;
    }

    // #4730: flush the cache mutations built up during the just-committed batch. Only called by the
    // execution AFTER the batch commits, so the cache reflects committed state only — a failed or
    // retried build never reaches here and its mutations are discarded at the next BuildBatchAsync.
    // (No-op for composite member batches, which wrote the cache during build and left this empty.)
    public void ApplyPendingCacheUpdates()
    {
        while (_pendingCacheUpdates.TryDequeue(out var update))
        {
            if (update.Remove)
            {
                update.Cache.TryRemove(update.Id);
            }
            else
            {
                update.Cache.Store(update.Id, update.Snapshot!);
            }
        }

        try
        {
            foreach (var pair in _caches.Enumerate())
            {
                pair.Value.CompactIfNecessary();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to compact aggregate caches for {ProjectionName}", Projection.Name);
        }
    }

    private void storeInCache(IAggregateCache<TId, TDoc> cache, TId id, TDoc snapshot)
    {
        if (_populateCacheImmediately)
        {
            cache.Store(id, snapshot);
        }
        else
        {
            _pendingCacheUpdates.Enqueue((cache, id, snapshot, false));
        }
    }

    private void removeFromCache(IAggregateCache<TId, TDoc> cache, TId id)
    {
        if (_populateCacheImmediately)
        {
            cache.TryRemove(id);
        }
        else
        {
            _pendingCacheUpdates.Enqueue((cache, id, default, true));
        }
    }

    private async Task processBatchAsync(CancellationToken cancellation, IProjectionBatch<TOperations, TQuerySession> batch, SliceGroup<TDoc, TId> group,
        Block<EventSliceExecution> builder)
    {
        var operations = batch.SessionForTenant(group.TenantId);
        var cache = CacheFor(group.TenantId);

        var needToBeFetched = new List<EventSlice<TDoc, TId>>();
        var storage = await operations.FetchProjectionStorageAsync<TDoc, TId>(group.TenantId, cancellation);

        group.Operations = operations;
        await Projection.EnrichEventsAsync(group, operations, cancellation);

        foreach (var slice in group.Slices)
        {
            // jasperfx#525: during a deferred rebuild the authoritative in-flight aggregate lives in the flush
            // accumulator (its write was buffered, never sent to the database), so a later range for the same
            // stream must read it back from there — otherwise it would reload null and lose the earlier pages.
            // Ids already flushed in an earlier window are absent here and fall through to the database load
            // below, which is correct because they were written there.
            if (_deferWrites && tryFindPendingSnapshot(group.TenantId, slice.Id, out var pending))
            {
                slice.Snapshot = pending;
                await builder.PostAsync(new EventSliceExecution(slice, operations, storage, cache));
            }
            // If you can find the snapshot in the cache, use that
            else if (cache.TryFind(slice.Id, out var snapshot))
            {
                slice.Snapshot = snapshot;

                await builder.PostAsync(new EventSliceExecution(slice, operations, storage, cache));
            }
            else
            {
                // Otherwise, this will need to be fetched
                needToBeFetched.Add(slice);
            }
        }

        var snapshots = await storage.LoadManyAsync(needToBeFetched.Select(x => x.Id).ToArray(), cancellation);
        foreach (var slice in needToBeFetched)
        {
            if (snapshots.TryGetValue(slice.Id, out var snapshot))
            {
                slice.Snapshot = snapshot;
            }

            await builder.PostAsync(new EventSliceExecution(slice, operations, storage, cache));
        }
    }

    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        executor = default!;
        return false;
    }

    ErrorHandlingOptions IGroupedProjectionRunner.ErrorHandlingOptions(ShardExecutionMode mode)
    {
        return _store.ErrorHandlingOptions(mode);
    }

    // Assume this is pointed at the correct tenant id from the get go
    // THIS IS ONLY USED FOR ASYNC!!!
    public async Task ApplyChangesAsync(ShardExecutionMode mode,
        IProjectionBatch batch,
        TOperations operations,
        EventSlice<TDoc, TId> slice,
        IProjectionStorage<TDoc, TId> storage,
        IAggregateCache<TId, TDoc> cache,
        CancellationToken cancellation)
    {
        if (slice.TenantId != storage.TenantId)
        {
            throw new InvalidOperationException(
                $"TenantId does not match from the slice '{slice.TenantId}' and storage '{storage.TenantId}'");
        }

        slice.FastForwardForCompacting();

        if (Projection.MatchesAnyDeleteType(slice.Events()))
        {
            if (mode == ShardExecutionMode.Continuous)
            {
                await processPossibleSideEffects(batch, operations, slice).ConfigureAwait(false);
            }

            // A DeleteEvent<T> registration deletes through this short-circuit rather than
            // through DetermineActionAsync/buildActionAsync. A pre-existing snapshot is what
            // makes this a real deletion and signals stream ownership; with no prior snapshot
            // buildActionAsync maps to ActionType.Nothing, so this stays a no-op.
            // See issue JasperFx/marten#4093.
            var ownsStream = slice.Snapshot != null;

            // jasperfx#525: buffer the delete tombstone (idempotent when replayed at flush) instead of writing
            // it now; the stream-archive rides the flush batch too. Cache is untouched because the accumulator
            // is the authoritative in-flight source during a deferred rebuild.
            if (_deferWrites)
            {
                if (ownsStream)
                {
                    slice.RecordAction(ActionType.Delete);
                    slice.Snapshot = default;
                }

                recordDeferredWrite(storage.TenantId, slice.Id, ActionType.Delete, default, null,
                    wouldArchiveStream(slice, ownsStream));
                return;
            }

            maybeArchiveStream(storage, slice, ownsStream);

            if (ownsStream)
            {
                // Record the delete and clear the snapshot so MarkSliceAction fans the synthetic
                // ProjectionDeleted<TDoc,TId> event to downstream composite stages. Without this,
                // ResultingAction stayed at Nothing (and slice.Snapshot non-null), so later stages
                // saw a stale Updated<TDoc> instead of the deletion — or nothing at all. This
                // mirrors the DetermineActionAsync delete path below. See issue JasperFx/jasperfx#483.
                slice.RecordAction(ActionType.Delete);
                slice.Snapshot = default;
                removeFromCache(cache, slice.Id);
            }

            storage.Delete(slice.Id);
            return;
        }

        var (snapshot, action) = await Projection.DetermineActionAsync(operations, slice.Snapshot, slice.Id, storage,
            slice.Events(), cancellation);

        slice.RecordAction(action);

        if (action == ActionType.Nothing)
        {
            return;
        }

        (var lastEvent, snapshot) = Projection.TryApplyMetadata(slice.Events(), snapshot, slice.Id, storage);

        // Ownership: either there was a pre-loaded snapshot or the slice's events
        // materialized one. Siblings that did neither skip the archive.
        // See issue JasperFx/marten#4093.
        var ownsResultingStream = slice.Snapshot != null || snapshot != null;

        // Set the resulting aggregate on the slice in EVERY mode. A composite stage fans an
        // Updated<TDoc> event to its downstream stages via MarkSliceAction, which only emits when
        // slice.Snapshot is non-null. Gating this on Continuous (as the side effects below are)
        // meant a composite rebuild/catch-up produced no upstream snapshot, so downstream stages
        // lost the synthetic Updated<TDoc>/References<T> events and threw NREs. The side effects
        // themselves (RaiseSideEffects -> raised events / published messages) stay Continuous-only
        // so a rebuild does not re-emit them. See marten#4729.
        slice.Snapshot = snapshot;

        // jasperfx#525: buffer the resulting write keyed by aggregate id (a later range overwrites this entry,
        // which is the dedup) rather than sending it to the database now. Side effects never run in a rebuild,
        // so nothing else here needs the batch. The buffered snapshot becomes the in-flight read for later
        // ranges (see processBatchAsync) and is materialized into a single op at flush.
        if (_deferWrites)
        {
            recordDeferredWrite(storage.TenantId, slice.Id, action, snapshot, lastEvent,
                wouldArchiveStream(slice, ownsResultingStream));
            return;
        }

        maybeArchiveStream(storage, slice, ownsStream: ownsResultingStream);

        if (mode == ShardExecutionMode.Continuous)
        {
            await processPossibleSideEffects(batch, operations, slice).ConfigureAwait(false);
        }

        // #4730: cache mutations route through storeInCache/removeFromCache so that for standalone
        // (Individual) batches they are deferred until ApplyPendingCacheUpdates runs after commit.
        switch (action)
        {
            case ActionType.Delete:
                removeFromCache(cache, slice.Id);
                storage.Delete(slice.Id);
                break;
            case ActionType.Store:
                storeInCache(cache, slice.Id, snapshot!);
                storage.StoreProjection(snapshot!, lastEvent, Projection.Scope);
                break;
            case ActionType.HardDelete:
                removeFromCache(cache, slice.Id);
                storage.HardDelete(snapshot!);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(snapshot!);
                storeInCache(cache, slice.Id, snapshot!);
                storage.StoreProjection(snapshot!, lastEvent, Projection.Scope);
                break;
            case ActionType.StoreThenSoftDelete:
                storage.StoreProjection(snapshot!, lastEvent, Projection.Scope);
                removeFromCache(cache, slice.Id);
                storage.Delete(slice.Id);
                break;
        }
    }

    public IAggregateCache<TId, TDoc> CacheFor(string tenantId)
    {
        if (_caches.TryFind(tenantId, out var cache))
        {
            return cache;
        }

        lock (_cacheLock)
        {
            if (_caches.TryFind(tenantId, out cache))
            {
                return cache;
            }

            cache = Projection.Options.CacheLimitPerTenant == 0
                ? new NulloAggregateCache<TId, TDoc>()
                : new RecentlyUsedCache<TId, TDoc> { Limit = Projection.Options.CacheLimitPerTenant };

            _caches = _caches.AddOrUpdate(tenantId, cache);

            return cache;
        }
    }

    private void maybeArchiveStream(IProjectionStorage<TDoc, TId> storage, EventSlice<TDoc, TId> slice, bool ownsStream)
    {
        if (Projection.Scope != AggregationScope.SingleStream) return;

        // Only the single-stream projection that actually owns the stream — as signalled
        // by a snapshot being present either before or after the slice is applied —
        // should archive the stream. In a composite projection with multiple single
        // stream children, sibling projections otherwise fire redundant (or phantom)
        // stream-archival operations. See issue JasperFx/marten#4093.
        if (!ownsStream) return;

        if (slice.Events().OfType<IEvent<Archived>>().Any())
        {
            storage.ArchiveStream(slice.Id, slice.TenantId);
        }
    }

    // jasperfx#525: would maybeArchiveStream have archived this stream? Used to record the archive intent onto
    // a buffered write so the archive rides the flush batch instead of being emitted per range.
    private bool wouldArchiveStream(EventSlice<TDoc, TId> slice, bool ownsStream)
        => Projection.Scope == AggregationScope.SingleStream
           && ownsStream
           && slice.Events().OfType<IEvent<Archived>>().Any();

    // jasperfx#525
    public bool DefersRebuildWrites(ShardExecutionMode mode, EventRange range)
        => mode == ShardExecutionMode.Rebuild
           && range.BatchBehavior == BatchBehavior.Individual
           && rebuildFlushThreshold > 0;

    // jasperfx#525
    public int DeferredWriteCount
    {
        get
        {
            lock (_bufferLock)
            {
                return _flush?.DirtyCount ?? 0;
            }
        }
    }

    // jasperfx#525: flush at the threshold, and always at the rebuild's target ceiling (the final range) so
    // progress reaches the ceiling and the rebuild completes even when the last window is already empty.
    public bool DeferredFlushDue(EventRange range)
    {
        if (!_deferWrites)
        {
            return false;
        }

        var reachedCeiling = range.Agent.HighWaterMark > 0 && range.SequenceCeiling >= range.Agent.HighWaterMark;

        lock (_bufferLock)
        {
            return reachedCeiling || (_flush?.DirtyCount ?? 0) >= rebuildFlushThreshold;
        }
    }

    private void recordDeferredWrite(string tenantId, TId id, ActionType action, TDoc? snapshot, IEvent? lastEvent,
        bool archiveStream)
    {
        lock (_bufferLock)
        {
            _flush!.Record(tenantId, id, new PendingWrite(action, snapshot, lastEvent, archiveStream));
        }
    }

    private bool tryFindPendingSnapshot(string tenantId, TId id, out TDoc? snapshot)
    {
        lock (_bufferLock)
        {
            if (_flush != null && _flush.TryGetPending(tenantId, id, out var write))
            {
                // #525: a buffered delete is a tombstone — a later range for the same stream must see the
                // aggregate as gone (null) rather than resurrecting the pre-delete snapshot from the buffer.
                snapshot = isRemoval(write.Action) ? default : write.Snapshot;
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    // #525: actions that leave no live row behind, so the aggregate is "gone" for read-through and must be
    // dropped from the flushed-id set (a later re-create is then a fresh INSERT, not a reflush/UPSERT).
    private static bool isRemoval(ActionType action)
        => action is ActionType.Delete or ActionType.HardDelete;

    // jasperfx#525: emit exactly one operation per pending aggregate into a fresh batch, execute it, and only
    // then advance progress to the ceiling and promote this window's ids to the flushed set. If the flush
    // itself fails the accumulator is left intact, so a retry (or a fresh rebuild) rewrites the same state.
    public async Task FlushDeferredRebuildWritesAsync(EventRange range, long ceiling, CancellationToken cancellation)
    {
        List<(string TenantId, TId Id, PendingWrite Write, bool PreviouslyFlushed)> entries;
        long windowFloor;
        lock (_bufferLock)
        {
            entries = _flush?.Drain().ToList() ?? new();
            windowFloor = _flush?.WindowFloor ?? range.SequenceFloor;
        }

        // Always build and execute a batch — even when there are no pending writes (e.g. the final range after a
        // threshold flush drained the window) — because the batch is what advances the projection's *committed*
        // progression in the database. MarkSuccessAsync only advances the agent's in-memory mark.
        //
        // The flush batch carries no events of its own — it only re-emits the accumulated snapshots — but a
        // store's StartProjectionBatchAsync may enumerate range.Events (e.g. Marten's natural-key hook), so give
        // it an empty (non-null) list. Its floor is the window floor (the current committed progression) so the
        // store's optimistic progression update `set last_seq_id = ceiling where last_seq_id = floor` matches.
        var flushRange = new EventRange(range.Agent, windowFloor, ceiling)
        {
            Events = new List<IEvent>()
        };
        var batch = await _store
            .StartProjectionBatchAsync(flushRange, _database, ShardExecutionMode.Rebuild, Projection.Options,
                cancellation).ConfigureAwait(false);

        var writeThrottle = range.Agent.BatchWriteThrottle;
        if (writeThrottle != null)
        {
            await writeThrottle.WaitAsync(cancellation).ConfigureAwait(false);
        }

        try
        {
            foreach (var group in entries.GroupBy(x => x.TenantId))
            {
                var operations = batch.SessionForTenant(group.Key);
                var storage = await operations.FetchProjectionStorageAsync<TDoc, TId>(group.Key, cancellation)
                    .ConfigureAwait(false);

                foreach (var entry in group)
                {
                    applyPendingWrite(storage, entry.Id, entry.Write, entry.PreviouslyFlushed);
                }
            }

            await batch.ExecuteAsync(cancellation).ConfigureAwait(false);
            await range.Agent.MarkSuccessAsync(ceiling).ConfigureAwait(false);

            lock (_bufferLock)
            {
                _flush?.Commit(ceiling);
            }
        }
        finally
        {
            writeThrottle.SafeRelease();
            await batch.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void applyPendingWrite(IProjectionStorage<TDoc, TId> storage, TId id, PendingWrite write,
        bool previouslyFlushed)
    {
        if (write.ArchiveStream)
        {
            storage.ArchiveStream(id, storage.TenantId);
        }

        switch (write.Action)
        {
            case ActionType.Delete:
                storage.Delete(id);
                break;
            case ActionType.Store:
                storage.StoreProjectionForRebuildFlush(write.Snapshot!, write.LastEvent, Projection.Scope,
                    previouslyFlushed);
                break;
            case ActionType.HardDelete:
                storage.HardDelete(write.Snapshot!);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(write.Snapshot!);
                storage.StoreProjectionForRebuildFlush(write.Snapshot!, write.LastEvent, Projection.Scope,
                    previouslyFlushed);
                break;
            case ActionType.StoreThenSoftDelete:
                storage.StoreProjectionForRebuildFlush(write.Snapshot!, write.LastEvent, Projection.Scope,
                    previouslyFlushed);
                storage.Delete(id);
                break;
        }
    }

    // jasperfx#525
    public void DiscardDeferredRebuildWrites()
    {
        lock (_bufferLock)
        {
            _flush = null;
            _deferWrites = false;
        }
    }

    // jasperfx#525: test seam mirroring what BuildBatchAsync does when a deferred rebuild is active, so unit
    // tests can drive ApplyChangesAsync/flush directly without standing up a full batch build.
    internal void BeginDeferredRebuildWindowForTesting()
    {
        _deferWrites = true;
        lock (_bufferLock)
        {
            _flush ??= new RebuildFlushWindow();
        }
    }

    internal readonly record struct PendingWrite(
        ActionType Action,
        TDoc? Snapshot,
        IEvent? LastEvent,
        bool ArchiveStream);

    // jasperfx#525: one flush window's worth of pending writes plus the set of ids already flushed earlier in
    // this rebuild. Not thread-safe on its own — always accessed under AggregationRunner._bufferLock.
    private sealed class RebuildFlushWindow
    {
        // Per tenant: the single latest pending write per aggregate id in the CURRENT window. A repeat write to
        // the same id overwrites the entry, which is the dedup that yields one op per aggregate per flush.
        private readonly Dictionary<string, Dictionary<TId, PendingWrite>> _pending = new();

        // Per tenant: ids already written in an EARLIER window this rebuild. A reflush of one of these routes as
        // an UPSERT rather than an INSERT.
        private readonly Dictionary<string, HashSet<TId>> _flushed = new();

        private bool _floorSet;

        public int DirtyCount { get; private set; }

        // The event-sequence floor of the CURRENT (un-flushed) window == the projection's committed progression
        // right now. The flush batch must key its optimistic progression update on this value (Marten does
        // `set last_seq_id = ceiling where last_seq_id = floor`), so it tracks the last flushed ceiling — seeded
        // from the first buffered range's floor, then advanced to each flushed ceiling.
        public long WindowFloor { get; private set; }

        public void EnsureFloor(long rangeFloor)
        {
            if (!_floorSet)
            {
                WindowFloor = rangeFloor;
                _floorSet = true;
            }
        }

        public void Record(string tenantId, TId id, PendingWrite write)
        {
            if (!_pending.TryGetValue(tenantId, out var map))
            {
                map = new Dictionary<TId, PendingWrite>();
                _pending[tenantId] = map;
            }

            if (!map.ContainsKey(id))
            {
                DirtyCount++;
            }

            map[id] = write;
        }

        public bool TryGetPending(string tenantId, TId id, out PendingWrite write)
        {
            if (_pending.TryGetValue(tenantId, out var map))
            {
                return map.TryGetValue(id, out write);
            }

            write = default;
            return false;
        }

        public IEnumerable<(string TenantId, TId Id, PendingWrite Write, bool PreviouslyFlushed)> Drain()
        {
            foreach (var (tenantId, map) in _pending)
            {
                var alreadyFlushed = _flushed.TryGetValue(tenantId, out var set) ? set : null;
                foreach (var (id, write) in map)
                {
                    yield return (tenantId, id, write, alreadyFlushed?.Contains(id) == true);
                }
            }
        }

        public void Commit(long ceiling)
        {
            // The just-flushed ceiling becomes the committed progression, so it is the next window's floor.
            WindowFloor = ceiling;
            _floorSet = true;

            foreach (var (tenantId, map) in _pending)
            {
                if (!_flushed.TryGetValue(tenantId, out var set))
                {
                    set = new HashSet<TId>();
                    _flushed[tenantId] = set;
                }

                foreach (var (id, write) in map)
                {
                    // #525: a store leaves a live row (a later window's write for it must UPSERT); a delete
                    // removes the row, so drop it from the flushed set — a re-create is then a fresh INSERT.
                    if (isRemoval(write.Action))
                    {
                        set.Remove(id);
                    }
                    else
                    {
                        set.Add(id);
                    }
                }
            }

            _pending.Clear();
            DirtyCount = 0;
        }
    }

    // Look at AggregateApplicationRuntime processPossibleSideEffects
    private async Task processPossibleSideEffects(IProjectionBatch batch, TOperations operations,
        EventSlice<TDoc, TId> slice)
    {
        await Projection.RaiseSideEffects(operations, slice.Id, slice);

        if (slice.RaisedEvents != null)
        {
            slice.BuildOperations(_store.Registry, batch, Projection.Scope);
        }

        if (slice.PublishedMessages != null)
        {
            foreach (var message in slice.PublishedMessages)
                await batch.PublishMessageAsync(message, slice.TenantId).ConfigureAwait(false);
        }

        // Independent path for messages enqueued with per-message metadata via
        // slice.PublishMessage(message, metadata). Metadata carries through to the
        // IMessageSink implementation (e.g. Wolverine), which maps it onto its
        // native delivery options.
        if (slice.PublishedMessagesWithMetadata != null)
        {
            foreach (var (message, metadata) in slice.PublishedMessagesWithMetadata)
                await batch.PublishMessageAsync(message, metadata).ConfigureAwait(false);
        }
    }

    private record EventSliceExecution(
        EventSlice<TDoc, TId> Slice,
        TOperations Operations,
        IProjectionStorage<TDoc, TId> Storage,
        IAggregateCache<TId, TDoc> Cache);
}