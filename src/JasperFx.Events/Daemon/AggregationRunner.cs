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
}

public class AggregationRunner<TDoc, TId, TOperations, TQuerySession> : IGroupedProjectionRunner where TOperations : TQuerySession
{
    private readonly IEventRegistry _events;
    private readonly AggregateApplication<TDoc, TQuerySession> _application;
    private readonly IEventSlicer<TDoc,TId> _slicer;

    // TODO -- do something to abstract AggregateApplication. 
    public AggregationRunner(IEventRegistry events, IAggregationProjection<TDoc, TOperations> projection, AggregateApplication<TDoc, TQuerySession> application, SliceBehavior sliceBehavior, IEventSlicer<TDoc, TId> slicer)
    {
        Projection = projection;
        SliceBehavior = sliceBehavior;
        _events = events;
        _application = application;

        _slicer = slicer;
    }

    public IReadOnlyList<Type> DeleteTypes { get; }

    public async ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public IAggregationProjection<TDoc, TOperations> Projection { get; }
    public SliceBehavior SliceBehavior { get; }
    public async Task<IProjectionBatch> BuildBatchAsync(EventRange range)
    {
        /*
         * Notes:
         * Start by adding a progression operation. See EventRangeExtensions.BuildProgressionOperation
         *    - Add to IProjectionBatch interface
         * Pull in TenantSliceRange.ConfigureUpdateBatch
         * Get TenantSliceGroup.Start
         *
         *
         * 
         */

        var groups = range.Groups.OfType<SliceGroup<TDoc, TId>>();
        

        throw new NotImplementedException();
    }

    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        throw new NotImplementedException();
    }

    // This will be a wrapper I guess.
    public IEventSlicer Slicer { get; }

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
        if (Projection.MatchesAnyDeleteType(slice))
        {
            if (mode == ShardExecutionMode.Continuous)
            {
                await processPossibleSideEffects(storage, slice).ConfigureAwait(false);
            }
            
            maybeArchiveStream(storage.EventDatabase, slice);
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

        maybeArchiveStream(storage.EventDatabase, slice);

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
        storage.StoreForAsync(aggregate, lastEvent, Slicer is ISingleStreamSlicer);


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
    
    private void maybeArchiveStream(IEventDatabase events, EventSlice<TDoc, TId> slice)
    {
        if (Slicer is ISingleStreamSlicer<TId> singleStreamSlicer && slice.Events().OfType<IEvent<Archived>>().Any())
        {
            singleStreamSlicer.ArchiveStream(events, slice.Id);
        }
    }

    // Look at AggregateApplicationRuntime processPossibleSideEffects
    private async Task processPossibleSideEffects(IProjectionStorage<TDoc,TOperations> storage, EventSlice<TDoc,TId> slice)
    {
        await Projection.RaiseSideEffects(storage.Operations, slice);
        
        if (slice.RaisedEvents != null)
        {
            // TODO -- change this to usage storage to just enqueue
            slice.BuildOperations(_events, storage, Projection.IsSingleStream());
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

// This will wrap ProjectionUpdateBatch & the right DocumentSession
public interface IProjectionStorage<TDoc, TOperations> : IEventStorageBuilder
{
    string TenantId { get; }
    
    // var operation = Storage.DeleteForId(slice.Id, slice.TenantId); and QueueOperation(). Watch ordering
    void MarkDeleted<TId>(TId sliceId);

    TOperations Operations { get; }
    
    IEventDatabase EventDatabase { get; }
    
    Task PublishMessageAsync(object message);

    // should we treat this as a transient error that should be retried,
    // or as an application failure?
    bool IsExceptionTransient(Exception ex);
        
    // Storage.SetIdentity(aggregate, slice.Id);
    // Versioning.TrySetVersion(aggregate, lastEvent);
    void SetIdentityAndVersion<TDoc, TId>(TDoc aggregate, TId sliceId, IEvent? lastEvent);
    
    /*
        if (Slicer is ISingleStreamSlicer && lastEvent != null && storageOperation is IRevisionedOperation op)
       {
           op.Revision = (int)lastEvent.Version;
           op.IgnoreConcurrencyViolation = true;
       }
     */
    void StoreForAsync(TDoc aggregate, IEvent? lastEvent, bool isSingleStream);
}
