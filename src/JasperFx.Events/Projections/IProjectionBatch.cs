using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

public interface IProjectionBatch : IAsyncDisposable
{
    Task ExecuteAsync(CancellationToken token);
    
    // TODO -- this needs to carry through the tenant id
    void QuickAppendEventWithVersion(StreamAction action, IEvent @event);
    void UpdateStreamVersion(StreamAction action);
    void QuickAppendEvents(StreamAction action);
    
    // This is for publishing side effects from event slices in aggregation projections
    Task PublishMessageAsync(object message);
}

public interface IProjectionBatch<TOperations, TQuerySession> : IProjectionBatch where TOperations : TQuerySession, IStorageOperations
{
    TOperations SessionForTenant(string tenantId);

    IProjectionStorage<TDoc, TId> ProjectionStorageFor<TDoc, TId>(string tenantId);
    IProjectionStorage<TDoc, TId> ProjectionStorageFor<TDoc, TId>();
}
