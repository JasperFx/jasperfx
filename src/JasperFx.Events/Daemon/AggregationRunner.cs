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

        var caches = new List<IAggregateCache<TId, TDoc>>();
        var groups = range.Groups.OfType<SliceGroup<TDoc, TId>>().ToArray();
        foreach (var group in groups)
        {
            await processBatchAsync(cancellation, batch, group, caches, builder);
        }

        await builder.WaitForCompletionAsync().ConfigureAwait(false);

        foreach (var group in groups)
        {
            foreach (var slice in group.Slices)
            {
                if (slice.Snapshot != null)
                {
                    range.MarkUpdated(group.TenantId, slice.Snapshot);
                }
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

        try
        {
            foreach (var cache in caches) cache.CompactIfNecessary();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to compact aggregate caches for {ProjectionName}", Projection.Name);
        }

        await Projection.EndBatchAsync();

        return batch;
    }

    private async Task processBatchAsync(CancellationToken cancellation, IProjectionBatch<TOperations, TQuerySession> batch, SliceGroup<TDoc, TId> group,
        List<IAggregateCache<TId, TDoc>> caches, Block<EventSliceExecution> builder)
    {
        var operations = batch.SessionForTenant(group.TenantId);
        var cache = CacheFor(group.TenantId);
        caches.Add(cache);

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

            maybeArchiveStream(storage, slice);
            storage.Delete(slice.Id);
            return;
        }

        var (snapshot, action) = await Projection.DetermineActionAsync(operations, slice.Snapshot, slice.Id, storage,
            slice.Events(), cancellation);
        if (action == ActionType.Nothing)
        {
            return;
        }

        (var lastEvent, snapshot) = Projection.TryApplyMetadata(slice.Events(), snapshot, slice.Id, storage);

        maybeArchiveStream(storage, slice);

        if (mode == ShardExecutionMode.Continuous)
        {
            // Need to set the aggregate in case it didn't exist upfront
            slice.Snapshot = snapshot;
            await processPossibleSideEffects(batch, operations, slice).ConfigureAwait(false);
        }

        switch (action)
        {
            case ActionType.Delete:
                cache.TryRemove(slice.Id);
                storage.Delete(slice.Id);
                break;
            case ActionType.Store:
                cache.Store(slice.Id, snapshot!);
                storage.StoreProjection(snapshot!, lastEvent, Projection.Scope);
                break;
            case ActionType.HardDelete:
                cache.TryRemove(slice.Id);
                storage.HardDelete(snapshot!);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(snapshot!);
                cache.Store(slice.Id, snapshot!);
                storage.StoreProjection(snapshot!, lastEvent, Projection.Scope);
                break;
            case ActionType.StoreThenSoftDelete:
                storage.StoreProjection(snapshot!, lastEvent, Projection.Scope);
                cache.TryRemove(slice.Id);
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

    private void maybeArchiveStream(IProjectionStorage<TDoc, TId> storage, EventSlice<TDoc, TId> slice)
    {
        if (Projection.Scope == AggregationScope.SingleStream && slice.Events().OfType<IEvent<Archived>>().Any())
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
    }

    private record EventSliceExecution(
        EventSlice<TDoc, TId> Slice,
        TOperations Operations,
        IProjectionStorage<TDoc, TId> Storage,
        IAggregateCache<TId, TDoc> Cache);
}