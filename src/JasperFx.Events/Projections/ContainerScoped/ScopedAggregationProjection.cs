using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Events.Projections.ContainerScoped;

internal class ScopedAggregationProjection<TSource, TDoc, TId, TOperations, TQuerySession> : IAggregationProjection<TDoc, TId, TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
    where TSource : JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>, IAggregationProjection<TDoc, TId, TOperations, TQuerySession>
    where TDoc : notnull
    where TId : notnull
{
    private readonly IServiceProvider _services;
    private readonly ScopedAggregationWrapper<TSource, TDoc, TId, TOperations, TQuerySession> _scopedAggregation;
    private AsyncServiceScope _scope;
    private TSource _inner = null!;

    public ScopedAggregationProjection(IServiceProvider services, ScopedAggregationWrapper<TSource, TDoc, TId, TOperations, TQuerySession> scopedAggregation)
    {
        _services = services;
        _scopedAggregation = scopedAggregation;
    }

    public ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice)
    {
        return _inner.RaiseSideEffects(operations, slice);
    }

    public Task EnrichEventsAsync(SliceGroup<TDoc, TId> group, TQuerySession querySession, CancellationToken cancellation)
    {
        return _inner.EnrichEventsAsync(group, querySession, cancellation);
    }

    public AggregationScope Scope => _scopedAggregation.Scope;
    public bool MatchesAnyDeleteType(IReadOnlyList<IEvent> events)
    {
        return _inner.MatchesAnyDeleteType(events);
    }

    public TDoc ApplyMetadata(TDoc aggregate, IEvent @event)
    {
        return _inner.ApplyMetadata(aggregate, @event);
    }

    public ValueTask<(TDoc?, ActionType)> DetermineActionAsync(TQuerySession session, TDoc? snapshot, TId identity, IIdentitySetter<TDoc, TId> identitySetter,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        return _inner.DetermineActionAsync(session, snapshot, identity, identitySetter, events, cancellation);
    }

    public Type[] AllEventTypes => _inner.AllEventTypes;
    public string Name => _scopedAggregation.Name;

    public AsyncOptions Options => _scopedAggregation.Options;
    public IEventSlicer BuildSlicer(TQuerySession session)
    {
        throw new NotSupportedException();
    }

    public (IEvent?, TDoc?) TryApplyMetadata(IReadOnlyList<IEvent> events, TDoc? aggregate, TId id, IIdentitySetter<TDoc, TId> identitySetter)
    {
        return _inner.TryApplyMetadata(events, aggregate, id, identitySetter);
    }

    public void StartBatch()
    {
        _scope = _services.CreateAsyncScope();
        _inner = _scope.ServiceProvider.GetRequiredService<TSource>();
    }

    public async ValueTask EndBatchAsync()
    {
        await _scope.DisposeAsync().ConfigureAwait(false);
    }
}