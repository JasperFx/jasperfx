using JasperFx.Events.Aggregation;

namespace JasperFx.Events.Daemon;

public interface IProjectionStorage<TDoc, TId> : IIdentitySetter<TDoc, TId>
{
    // This will wrap ProjectionUpdateBatch & the right DocumentSession
    
    string TenantId { get; }
    
    void HardDelete(TDoc snapshot);
    void UnDelete(TDoc snapshot);
    void Store(TDoc snapshot);
    void Delete(TId identity);
    
    void HardDelete(TDoc snapshot, string tenantId);
    void UnDelete(TDoc snapshot, string tenantId);
    void Store(TDoc snapshot, TId id, string tenantId);
    void Delete(TId identity, string tenantId);

    Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities, CancellationToken cancellationToken);

    void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope);
    void ArchiveStream(TId sliceId, string tenantId);
    Task<TDoc> LoadAsync(TId id, CancellationToken cancellation);

    /// <summary>
    /// jasperfx#525: store a projected document as part of a deferred rebuild flush. When
    /// <paramref name="previouslyFlushed"/> is false the aggregate is appearing for the first time this
    /// rebuild (post-TRUNCATE), so a store may route it through an INSERT-only fast path (e.g. binary COPY);
    /// when true the aggregate was already written in an earlier flush window (an overflow reflush) and must
    /// be routed as an UPSERT. The default implementation ignores the hint and behaves exactly like
    /// <see cref="StoreProjection"/>, so a store that has not opted into the optimization stays correct.
    /// </summary>
    void StoreProjectionForRebuildFlush(TDoc aggregate, IEvent? lastEvent, AggregationScope scope,
        bool previouslyFlushed)
        => StoreProjection(aggregate, lastEvent, scope);
}

public static class ProjectionStorageExtensions
{
    public static void ApplyInline<TDoc, TId>(this IProjectionStorage<TDoc, TId> storage, TDoc? snapshot,
        ActionType action, TId id, string tenantId)
    {
        switch (action)
        {
            case ActionType.Delete:
                storage.Delete(id, tenantId);
                break;
            case ActionType.Store:
                storage.Store(snapshot!, id, tenantId);
                break;
            case ActionType.HardDelete:
                storage.HardDelete(snapshot!, tenantId);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(snapshot!, tenantId);
                storage.Store(snapshot!, id, tenantId);
                break;
            case ActionType.StoreThenSoftDelete:
                storage.Store(snapshot!, id, tenantId);
                storage.Delete(id, tenantId);
                break;
        }
    }
}