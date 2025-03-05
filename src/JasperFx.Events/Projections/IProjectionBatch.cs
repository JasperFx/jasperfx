using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

public interface IProjectionBatch : IAsyncDisposable
{
    Task ExecuteAsync(CancellationToken token);
}

public interface IProjectionBatch<TOperations, TQuerySession> : IProjectionBatch where TOperations : TQuerySession
{
    /*
     * Notes
     * This will encapsulate Marten's ProjectionUpdateBatch, but spin out new DocumentSession as necessary
     * Follows the entire event range
     * 
     */
    
    TOperations SessionForTenant(string tenantId);

    IProjectionStorage<TDoc, TOperations> ProjectionStorageFor<TDoc>(string tenantId);
    
    // TODO -- add methods here to mark the progression as well?
}
