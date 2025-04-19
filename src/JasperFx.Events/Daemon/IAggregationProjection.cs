using JasperFx.Core;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Options;

namespace JasperFx.Events.Daemon;


public enum AggregationScope
{
    SingleStream,
    MultiStream
}

public interface IAggregationProjection<TDoc, TId, in TOperations, in TQuerySession> where TOperations : TQuerySession
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
    
    bool MatchesAnyDeleteType(IReadOnlyList<IEvent> events);
    TDoc ApplyMetadata(TDoc aggregate, IEvent @event);

    ValueTask<(TDoc?, ActionType)> DetermineActionAsync(TQuerySession session,
        TDoc? snapshot,
        TId identity,
        IIdentitySetter<TDoc, TId> identitySetter,
        IReadOnlyList<IEvent> events,
        CancellationToken cancellation);
    
    Type[] AllEventTypes { get; }
    string Name { get; }

    (IEvent?, TDoc?) TryApplyMetadata(IReadOnlyList<IEvent> events,
        TDoc? aggregate,
        TId id,
        IIdentitySetter<TDoc, TId> identitySetter);
    
    AsyncOptions Options { get; }

    IEventSlicer BuildSlicer(TQuerySession session);
    void StartBatch();
    ValueTask EndBatchAsync();
}