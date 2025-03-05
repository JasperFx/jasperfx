using System.Threading.Tasks.Dataflow;
using JasperFx.Events.Grouping;
using JasperFx.Events.NewStuff;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public interface IAggregationProjection<TDoc, TOperations>
{
    /// <summary>
    /// Use to create "side effects" when running an aggregation (single stream, custom projection, multi-stream)
    /// asynchronously in a continuous mode (i.e., not in rebuilds)
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="slice"></param>
    /// <returns></returns>
    ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice);

    bool IsSingleStream();
    
    bool MatchesAnyDeleteType(IEventSlice slice);
    TDoc ApplyMetadata(TDoc aggregate, IEvent @event);
    
    IEventSlicer Slicer { get; }
}

public class AggregationRunner<TDoc, TId, TOperations, TQuerySession> : IGroupedProjectionRunner where TOperations : TQuerySession
{
    private readonly IEventStorage<TOperations, TQuerySession> _storage;
    private readonly IEventDatabase _database;
    private readonly AggregateApplication<TDoc, TQuerySession> _application;

    // TODO -- do something to abstract AggregateApplication. 
    public AggregationRunner(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database,
        IAggregationProjection<TDoc, TOperations> projection, AggregateApplication<TDoc, TQuerySession> application,
        SliceBehavior sliceBehavior)
    {
        Projection = projection;
        SliceBehavior = sliceBehavior;
        _storage = storage;
        _database = database;
        _application = application;

    }

    public IEventSlicer Slicer => Projection.Slicer;

    public async ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public IAggregationProjection<TDoc, TOperations> Projection { get; }
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
        
        var aggregate = slice.Aggregate;
        
        // Does the aggregate already exist before the events are applied?
        var exists = aggregate != null;

        foreach (var @event in slice.Events())
        {
            if (@event is IEvent<Archived>) break;

            try
            {
                if (aggregate == null)
                {
                    aggregate = await _application.Create(@event, storage.Operations, cancellation).ConfigureAwait(false);
                }
                else
                {
                    aggregate = await _application.ApplyAsync(aggregate, @event, storage.Operations, cancellation).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // Should the exception be passed up for potential
                // retries?
                if (storage.IsExceptionTransient(e)) throw;
                
                throw new ApplyEventException(@event, e);
            }
        }

        (var lastEvent, aggregate) = tryApplyMetadata(slice, aggregate, storage);

        maybeArchiveStream(storage, slice);

        if (mode == ShardExecutionMode.Continuous)
        {
            // Need to set the aggregate in case it didn't exist upfront
            slice.Aggregate = aggregate;
            await processPossibleSideEffects(storage, slice).ConfigureAwait(false);
        }
        
        // Delete the aggregate *if* it existed prior to these events
        if (aggregate == null)
        {
            if (exists)
            {
                storage.MarkDeleted(slice.Id);
            }

            return;
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
        
        // TODO -- just ask IAggregationProjection if it's single stream
        storage.StoreForAsync(aggregate, lastEvent, Projection.Slicer is ISingleStreamSlicer);


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
                aggregate = (TDoc)Projection.ApplyMetadata(aggregate, @event);
            }
        }

        return (lastEvent, aggregate);
    }
    
    private void maybeArchiveStream(IProjectionStorage<TDoc, TOperations> storage, EventSlice<TDoc, TId> slice)
    {
        if (Projection.IsSingleStream())
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
            // TODO -- change this to usage storage to just enqueue
            slice.BuildOperations(_storage.Registry, storage, Projection.IsSingleStream());
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