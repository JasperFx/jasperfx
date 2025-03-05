using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Daemon;

public interface IProjectionStorage<TDoc, TOperations> : IEventStorageBuilder
{
    // This will wrap ProjectionUpdateBatch & the right DocumentSession
    
    string TenantId { get; }
    
    // var operation = Storage.DeleteForId(slice.Id, slice.TenantId); and QueueOperation(). Watch ordering
    void MarkDeleted<TId>(TId aggregateId);

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
    void ArchiveStream<TId>(TId sliceId);
}