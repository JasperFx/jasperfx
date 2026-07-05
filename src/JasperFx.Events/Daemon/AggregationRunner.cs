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
            // If you can find the snapshot in the cache, use that
            if (cache.TryFind(slice.Id, out var snapshot))
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
        maybeArchiveStream(storage, slice, ownsStream: slice.Snapshot != null || snapshot != null);

        // Set the resulting aggregate on the slice in EVERY mode. A composite stage fans an
        // Updated<TDoc> event to its downstream stages via MarkSliceAction, which only emits when
        // slice.Snapshot is non-null. Gating this on Continuous (as the side effects below are)
        // meant a composite rebuild/catch-up produced no upstream snapshot, so downstream stages
        // lost the synthetic Updated<TDoc>/References<T> events and threw NREs. The side effects
        // themselves (RaiseSideEffects -> raised events / published messages) stay Continuous-only
        // so a rebuild does not re-emit them. See marten#4729.
        slice.Snapshot = snapshot;

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

    // Look at AggregateApplicationRuntime processPossibleSideEffects
    private async Task processPossibleSideEffects(IProjectionBatch batch, TOperations operations,
        EventSlice<TDoc, TId> slice)
    {
        await Projection.RaiseSideEffects(operations, slice);

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