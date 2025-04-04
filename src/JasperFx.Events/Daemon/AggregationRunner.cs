using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public class AggregationRunner<TDoc, TId, TOperations, TQuerySession> : IGroupedProjectionRunner where TOperations : TQuerySession, IStorageOperations
{
    private readonly IEventStorage<TOperations, TQuerySession> _storage;
    private readonly IEventDatabase _database;
    private readonly ILogger _logger;

    public AggregationRunner(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database,
        IAggregationProjection<TDoc, TId, TOperations, TQuerySession> projection,
        SliceBehavior sliceBehavior, IEventSlicer slicer, ILogger logger)
    {
        Projection = projection;
        SliceBehavior = sliceBehavior;
        _storage = storage;
        _database = database;
        _logger = logger;
        Slicer = slicer;
    }

    public IEventSlicer Slicer { get; }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public IAggregationProjection<TDoc, TId, TOperations, TQuerySession> Projection { get; }
    public SliceBehavior SliceBehavior { get; }
    public async Task<IProjectionBatch> BuildBatchAsync(EventRange range, ShardExecutionMode mode,
        CancellationToken cancellation)
    {
        // TODO -- the projection batch wrapper will really need to know how to dispose all sessions built
        var batch = await _storage.StartProjectionBatchAsync(range, _database, mode, cancellation);

        if (SliceBehavior == SliceBehavior.JustInTime)
        {
            // TODO -- instrument this maybe?
            // This will need to pass in the database somehow for slicers that use a Marten database
            await range.SliceAsync(Slicer);
        }
        
        var builder = new ActionBlock<EventSliceExecution>(async execution =>
        {
            if (cancellation.IsCancellationRequested) return;
        
            await ApplyChangesAsync(mode, batch, execution.Operations, execution.Slice, execution.Storage, execution.Cache, cancellation);
        }, new ExecutionDataflowBlockOptions{CancellationToken = cancellation});

        var caches = new List<IAggregateCache<TId, TDoc>>();
        var groups = range.Groups.OfType<SliceGroup<TDoc, TId>>();
        foreach (var group in groups)
        {
            var operations = batch.SessionForTenant(group.TenantId);
            var cache = Projection.CacheFor(group.TenantId);
            caches.Add(cache);

            var needToBeFetched = new List<EventSlice<TDoc, TId>>(group.Slices);
            var storage = await operations.FetchProjectionStorageAsync<TDoc, TId>(group.TenantId, cancellation);
            
            foreach (var slice in group.Slices)
            {
                // If you can find the snapshot in the cache, use that
                if (cache.TryFind(slice.Id, out var snapshot))
                {
                    slice.Aggregate = snapshot;
                    
                    builder.Post(new EventSliceExecution(slice, operations, storage, cache));
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
                    slice.Aggregate = snapshot;
                }
                
                builder.Post(new EventSliceExecution(slice, operations, storage, cache));
            }
        }
        
        builder.Complete();
        await builder.Completion.ConfigureAwait(false);

        try
        {
            foreach (var cache in caches)
            {
                cache.CompactIfNecessary();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to compact aggregate caches for {ProjectionName}", Projection.Name);
        }

        return batch;
    }

    private record EventSliceExecution(EventSlice<TDoc, TId> Slice, TOperations Operations, IProjectionStorage<TDoc, TId> Storage, IAggregateCache<TId, TDoc> Cache);

    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        // TODO -- revisit this
        executor = default;
        return false;
    }

    public async Task EnsureStorageExists(CancellationToken token)
    {
        // TODO -- encapsulate this inside the async shard creation instead
        throw new NotImplementedException();
    }

    ErrorHandlingOptions IGroupedProjectionRunner.ErrorHandlingOptions(ShardExecutionMode mode)
    {
        return _storage.ErrorHandlingOptions(mode);
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
            throw new InvalidOperationException(
                $"TenantId does not match from the slice '{slice.TenantId}' and storage '{storage.TenantId}'");
        
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

        var action = await Projection.DetermineActionAsync(operations, slice.Aggregate, slice.Id, storage, slice.Events(), cancellation);
        if (action.Type == ActionType.Nothing) return;

        var snapshot = action.Snapshot;
        (var lastEvent, snapshot) = Projection.TryApplyMetadata(slice.Events(), snapshot, slice.Id, storage);

        maybeArchiveStream(storage, slice);

        if (mode == ShardExecutionMode.Continuous)
        {
            // Need to set the aggregate in case it didn't exist upfront
            slice.Aggregate = snapshot;
            await processPossibleSideEffects(batch, operations, slice).ConfigureAwait(false);
        }

        switch (action.Type)
        {
            case ActionType.Delete:
                cache.TryRemove(slice.Id);
                storage.Delete(slice.Id);
                break;
            case ActionType.Store:
                cache.Store(slice.Id, snapshot);
                storage.StoreProjection(snapshot, lastEvent, Projection.Scope);
                break;
            case ActionType.HardDelete:
                cache.TryRemove(slice.Id);
                storage.HardDelete(snapshot);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(snapshot);
                cache.Store(slice.Id, snapshot);
                storage.StoreProjection(snapshot, lastEvent, Projection.Scope);
                break;
            case ActionType.StoreThenSoftDelete:
                storage.StoreProjection(snapshot, lastEvent, Projection.Scope);
                cache.TryRemove(slice.Id);
                storage.Delete(slice.Id);
                break;
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
    private async Task processPossibleSideEffects(IProjectionBatch batch, TOperations operations, EventSlice<TDoc,TId> slice)
    {
        await Projection.RaiseSideEffects(operations, slice);
        
        if (slice.RaisedEvents != null)
        {
            slice.BuildOperations(_storage.Registry, batch, Projection.Scope);
        }

        if (slice.PublishedMessages != null)
        {
            foreach (var message in slice.PublishedMessages)
            {
                await batch.PublishMessageAsync(message).ConfigureAwait(false);
            }
        }
    }
}