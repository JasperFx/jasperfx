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

    /*
        if (Slicer is ISingleStreamSlicer && lastEvent != null && storageOperation is IRevisionedOperation op)
       {
           op.Revision = (int)lastEvent.Version;
           op.IgnoreConcurrencyViolation = true;
       }
     */
    void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope);
    void ArchiveStream(TId sliceId, string tenantId);
    Task<TDoc> LoadAsync(TId id, CancellationToken cancellation);
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