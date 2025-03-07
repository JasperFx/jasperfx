using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Daemon;

public interface IProjectionStorage<TDoc, TId> 
{
    // This will wrap ProjectionUpdateBatch & the right DocumentSession
    
    string TenantId { get; }
    
    // var operation = Storage.DeleteForId(slice.Id, slice.TenantId); and QueueOperation(). Watch ordering
    void MarkDeleted(TId aggregateId);

    void HardDelete(TDoc snapshot);
    void UnDelete(TDoc snapshot);
    void Store(TDoc snapshot);
    void Delete(TId identity);

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
    void StoreForAsync(TDoc aggregate, IEvent? lastEvent, AggregationScope isSingleStream);
    void ArchiveStream(TId sliceId);
}