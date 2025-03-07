using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Daemon;

public interface IProjectionStorage<TDoc, TId> 
{
    // This will wrap ProjectionUpdateBatch & the right DocumentSession
    
    string TenantId { get; }
    
    void HardDelete(TDoc snapshot);
    void UnDelete(TDoc snapshot);
    void Store(TDoc snapshot);
    void Delete(TId identity);
    
    void HardDelete(TDoc snapshot, string tenantId);
    void UnDelete(TDoc snapshot, string tenantId);
    void Store(TDoc snapshot, string tenantId);
    void Delete(TId identity, string tenantId);

    Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities, CancellationToken cancellationToken);
    
    // Storage.SetIdentity(aggregate, slice.Id);
    // Versioning.TrySetVersion(aggregate, lastEvent);
    void SetIdentityAndVersion(TDoc aggregate, TId sliceId, IEvent? lastEvent);
    
    /*
        if (Slicer is ISingleStreamSlicer && lastEvent != null && storageOperation is IRevisionedOperation op)
       {
           op.Revision = (int)lastEvent.Version;
           op.IgnoreConcurrencyViolation = true;
       }
     */
    void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope isSingleStream);
    void ArchiveStream(TId sliceId);
    Task<TDoc> LoadAsync(TId id, CancellationToken cancellation);
}

public static class ProjectionStorageExtensions
{
    public static void ApplyInline<TDoc, TId>(this IProjectionStorage<TDoc, TId> storage, SnapshotAction<TDoc> action, TId id, string tenantId)
    {
        switch (action.Type)
        {
            case ActionType.Delete:
                storage.Delete(id, tenantId);
                break;
            case ActionType.Store:
                storage.Store(action.Snapshot, tenantId);
                break;
            case ActionType.HardDelete:
                storage.HardDelete(action.Snapshot, tenantId);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(action.Snapshot, tenantId);
                storage.Store(action.Snapshot, tenantId);
                break;
        }
    }
}