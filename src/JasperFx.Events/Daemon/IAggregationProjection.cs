using JasperFx.Events.Grouping;
using Microsoft.Extensions.Options;

namespace JasperFx.Events.Daemon;


public enum AggregationScope
{
    SingleStream,
    MultiStream
}

public interface IAggregationProjection<TDoc, TId, TOperations, TQuerySession> where TOperations : TQuerySession
{
    /// <summary>
    /// Use to create "side effects" when running an aggregation (single stream, custom projection, multi-stream)
    /// asynchronously in a continuous mode (i.e., not in rebuilds)
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="slice"></param>
    /// <returns></returns>
    ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice);

    AggregationScope Scope { get; }
    
    bool MatchesAnyDeleteType(IEventSlice slice);
    TDoc ApplyMetadata(TDoc aggregate, IEvent @event);

    ValueTask<SnapshotAction<TDoc>> ApplyAsync(TQuerySession session,
        TDoc? snapshot,
        TId identity,
        IReadOnlyList<IEvent> events,
        CancellationToken cancellation);
}