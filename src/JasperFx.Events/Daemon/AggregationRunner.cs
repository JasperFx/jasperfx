using System.Threading.Tasks.Dataflow;
using JasperFx.Events.Grouping;
using JasperFx.Events.NewStuff;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class AggregationRunner<TDoc, TId, TOperations, TQuerySession> : IGroupedProjectionRunner where TOperations : TQuerySession, IStorageOperations
{
    private readonly IEventStorage<TOperations, TQuerySession> _storage;
    private readonly IEventDatabase _database;
    
    public AggregationRunner(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database,
        IAggregationProjection<TDoc, TId, TOperations, TQuerySession> projection,
        SliceBehavior sliceBehavior, IEventSlicer slicer)
    {
        Projection = projection;
        SliceBehavior = sliceBehavior;
        _storage = storage;
        _database = database;
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
        
            await ApplyChangesAsync(mode, batch, execution.Operations, execution.Slice, execution.Storage, cancellation);
        }, new ExecutionDataflowBlockOptions{CancellationToken = cancellation});
        
        var groups = range.Groups.OfType<SliceGroup<TDoc, TId>>();
        foreach (var group in groups)
        {
            var operations = batch.SessionForTenant(group.TenantId);
            var storage = operations.ProjectionStorageFor<TDoc, TId>(group.TenantId);
            foreach (var slice in group.Slices)
            {
                builder.Post(new EventSliceExecution(slice, operations, storage));
            }
        }
        
        builder.Complete();
        await builder.Completion.ConfigureAwait(false);

        return batch;
    }

    private record EventSliceExecution(EventSlice<TDoc, TId> Slice, TOperations Operations, IProjectionStorage<TDoc, TId> Storage);

    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        throw new NotImplementedException();
    }

    // TODO -- push this down to IEventStorage
    public ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode)
    {
        throw new NotImplementedException();
    }

    public async Task EnsureStorageExists(CancellationToken token)
    {
        // TODO -- encapsulate this inside the async shard creation instead
        throw new NotImplementedException();
    }

    // Assume this is pointed at the correct tenant id from the get go
    // THIS IS ONLY USED FOR ASYNC!!!
    public async Task ApplyChangesAsync(
        ShardExecutionMode mode, 
        IProjectionBatch batch,
        TOperations operations,
        EventSlice<TDoc, TId> slice, 
        IProjectionStorage<TDoc, TId> storage, 
        CancellationToken cancellation)
    {
        if (slice.TenantId != storage.TenantId)
            throw new InvalidOperationException(
                $"TenantId does not match from the slice '{slice.TenantId}' and storage '{storage.TenantId}'");
        
        if (Projection.MatchesAnyDeleteType(slice))
        {
            if (mode == ShardExecutionMode.Continuous)
            {
                await processPossibleSideEffects(batch, operations, slice).ConfigureAwait(false);
            }
            
            maybeArchiveStream(storage, slice);
            storage.Delete(slice.Id);
            return;
        }

        var action = await Projection.ApplyAsync(operations, slice.Aggregate, slice.Id, slice.Events(), cancellation);
        if (action.Type == ActionType.Nothing) return;

        var snapshot = action.Snapshot;
        (var lastEvent, snapshot) = tryApplyMetadata(slice, snapshot, storage);

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
                storage.Delete(slice.Id);
                break;
            case ActionType.Store:
                storage.StoreProjection(snapshot, lastEvent, Projection.Scope);
                break;
            case ActionType.HardDelete:
                storage.HardDelete(snapshot);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(snapshot);
                storage.StoreProjection(snapshot, lastEvent, Projection.Scope);
                break;
        }

    }
    
    private (IEvent?, TDoc?) tryApplyMetadata(EventSlice<TDoc, TId> slice, TDoc? aggregate,
        IProjectionStorage<TDoc, TId> storage)
    {
        var lastEvent = slice.Events().LastOrDefault();
        if (aggregate != null)
        {
            // TODO -- let's have this encapsulated within AggregationProjectionBase
            storage.SetIdentityAndVersion(aggregate, slice.Id, lastEvent);

            foreach (var @event in slice.Events())
            {
                aggregate = Projection.ApplyMetadata(aggregate, @event);
            }
        }

        return (lastEvent, aggregate);
    }
    
    private void maybeArchiveStream(IProjectionStorage<TDoc, TId> storage, EventSlice<TDoc, TId> slice)
    {
        if (Projection.Scope == AggregationScope.SingleStream)
        {
            storage.ArchiveStream(slice.Id);
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