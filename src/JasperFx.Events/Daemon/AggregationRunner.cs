using System.Threading.Tasks.Dataflow;
using JasperFx.Events.Grouping;
using JasperFx.Events.NewStuff;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class AggregationRunner<TDoc, TId, TOperations, TQuerySession> : IGroupedProjectionRunner where TOperations : TQuerySession
{
    private readonly IEventStorage<TOperations, TQuerySession> _storage;
    private readonly IEventDatabase _database;
    
    public AggregationRunner(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database,
        IAggregationProjection<TDoc, TId, TOperations> projection,
        SliceBehavior sliceBehavior)
    {
        Projection = projection;
        SliceBehavior = sliceBehavior;
        _storage = storage;
        _database = database;

    }

    public IEventSlicer Slicer => Projection.Slicer;

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public IAggregationProjection<TDoc, TId, TOperations> Projection { get; }
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
            await range.SliceAsync(Projection.Slicer);
        }

        var builder = new ActionBlock<EventSliceExecution>(async execution =>
        {
            if (cancellation.IsCancellationRequested) return;

            await ApplyChangesAsync(mode, execution.Slice, execution.Storage, cancellation);
        }, new ExecutionDataflowBlockOptions{CancellationToken = cancellation});
        
        var groups = range.Groups.OfType<SliceGroup<TDoc, TId>>();
        foreach (var group in groups)
        {
            var storage = batch.ProjectionStorageFor<TDoc>(group.TenantId);
            foreach (var slice in group.Slices)
            {
                builder.Post(new EventSliceExecution(slice, storage));
            }
        }
        
        builder.Complete();
        await builder.Completion.ConfigureAwait(false);

        return batch;
    }

    private record EventSliceExecution(EventSlice<TDoc, TId> Slice, IProjectionStorage<TDoc, TOperations> Storage);

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
    public async Task ApplyChangesAsync(ShardExecutionMode mode, EventSlice<TDoc, TId> slice, IProjectionStorage<TDoc, TOperations> storage, CancellationToken cancellation)
    {
        if (slice.TenantId != storage.TenantId)
            throw new InvalidOperationException(
                $"TenantId does not match from the slice '{slice.TenantId}' and storage '{storage.TenantId}'");
        
        if (Projection.MatchesAnyDeleteType(slice))
        {
            if (mode == ShardExecutionMode.Continuous)
            {
                await processPossibleSideEffects(storage, slice).ConfigureAwait(false);
            }
            
            maybeArchiveStream(storage, slice);
            storage.MarkDeleted(slice.Id);
            return;
        }

        var action = await Projection.ApplyAsync(slice.Aggregate, slice.Id, slice.Events());
        if (action.Type == ActionType.Nothing) return;

        var snapshot = action.Snapshot;
        (var lastEvent, snapshot) = tryApplyMetadata(slice, snapshot, storage);

        maybeArchiveStream(storage, slice);

        if (mode == ShardExecutionMode.Continuous)
        {
            // Need to set the aggregate in case it didn't exist upfront
            slice.Aggregate = snapshot;
            await processPossibleSideEffects(storage, slice).ConfigureAwait(false);
        }

        switch (action.Type)
        {
            case ActionType.Delete:
                storage.MarkDeleted(slice.Id);
                break;
            case ActionType.Store:
                storage.StoreForAsync(snapshot, lastEvent, Projection.AggregationScope);
                break;
            case ActionType.HardDelete:
                storage.HardDelete(snapshot);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(snapshot);
                storage.StoreForAsync(snapshot, lastEvent, Projection.AggregationScope);
                break;
        }

        /*
         * Encapsulates
        var storageOperation = Storage.Upsert(aggregate, session, slice.TenantId);
           if (Slicer is ISingleStreamSlicer && lastEvent != null && storageOperation is IRevisionedOperation op)
           {
               op.Revision = (int)lastEvent.Version;
               op.IgnoreConcurrencyViolation = true;
           }

           session.QueueOperation(storageOperation);
         */

    }
    
    private (IEvent?, TDoc?) tryApplyMetadata(EventSlice<TDoc, TId> slice, TDoc? aggregate,
        IProjectionStorage<TDoc, TOperations> storage)
    {
        var lastEvent = slice.Events().LastOrDefault();
        if (aggregate != null)
        {
            storage.SetIdentityAndVersion(aggregate, slice.Id, lastEvent);

            foreach (var @event in slice.Events())
            {
                aggregate = Projection.ApplyMetadata(aggregate, @event);
            }
        }

        return (lastEvent, aggregate);
    }
    
    private void maybeArchiveStream(IProjectionStorage<TDoc, TOperations> storage, EventSlice<TDoc, TId> slice)
    {
        if (Projection.AggregationScope == AggregationScope.SingleStream)
        {
            storage.ArchiveStream(slice.Id);
        }
    }

    // Look at AggregateApplicationRuntime processPossibleSideEffects
    private async Task processPossibleSideEffects(IProjectionStorage<TDoc,TOperations> storage, EventSlice<TDoc,TId> slice)
    {
        await Projection.RaiseSideEffects(storage.Operations, slice);
        
        if (slice.RaisedEvents != null)
        {
            slice.BuildOperations(_storage.Registry, storage, Projection.AggregationScope);
        }

        if (slice.PublishedMessages != null)
        {
            foreach (var message in slice.PublishedMessages)
            {
                await storage.PublishMessageAsync(message).ConfigureAwait(false);
            }
        }
    }
}