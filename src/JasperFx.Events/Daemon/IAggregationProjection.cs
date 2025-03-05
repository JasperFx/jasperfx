using JasperFx.Events.Grouping;

namespace JasperFx.Events.Daemon;

public enum ActionType
{
    Store,
    Delete,
    UnDeleteAndStore,
    Nothing,
    HardDelete
}

public interface ISnapshotAction
{
    ActionType Type { get; }
}

public record SnapshotAction<T>(T Snapshot, ActionType Type) : ISnapshotAction;

public record Store<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.Store);

public record Delete<TDoc, TId>(TDoc Snapshot, TId Identity) : SnapshotAction<TDoc>(Snapshot, ActionType.Store);

public record UnDeleteAndStore<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.UnDeleteAndStore);

public record Nothing<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.Nothing);

public record HardDelete<T>(T Snapshot) : SnapshotAction<T>(Snapshot, ActionType.HardDelete);

public enum AggregationType
{
    SingleStream,
    MultiStream
}

// This won't necessarily be the projection definition itself, but an object built by SingleStream/MultiStreamProjection
// specifically for async projections
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

    AggregationType AggregationType { get; }
    
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