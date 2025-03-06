using JasperFx.Events.Grouping;
using Microsoft.Extensions.Options;

namespace JasperFx.Events.Daemon;


public enum AggregationScope
{
    SingleStream,
    MultiStream
}

public interface IAggregationProjection<TDoc, TId, TOperations>
{
    /// <summary>
    /// Use to create "side effects" when running an aggregation (single stream, custom projection, multi-stream)
    /// asynchronously in a continuous mode (i.e., not in rebuilds)
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="slice"></param>
    /// <returns></returns>
    ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice);

    AggregationScope AggregationScope { get; }
    
    bool MatchesAnyDeleteType(IEventSlice slice);
    TDoc ApplyMetadata(TDoc aggregate, IEvent @event);
    
    IEventSlicer Slicer { get; }

    ValueTask<SnapshotAction<TDoc>> ApplyAsync(TDoc? snapshot, TId identity, IReadOnlyList<IEvent> events);

    /*
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
     */
}