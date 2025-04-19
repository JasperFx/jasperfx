using JasperFx.Events.Daemon;

namespace JasperFx.Events;

public interface IStorageOperations : IAsyncDisposable
{
    Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
        CancellationToken cancellationToken);
    Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(CancellationToken cancellationToken);
}