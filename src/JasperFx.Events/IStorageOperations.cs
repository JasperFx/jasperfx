using JasperFx.Events.Daemon;

namespace JasperFx.Events;

public interface IStorageOperations : IAsyncDisposable
{
    Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
        CancellationToken cancellationToken);

    bool EnableSideEffectsOnInlineProjections { get; }

    ValueTask<IMessageSink> GetOrStartMessageSink();

    /// <summary>
    /// Service provider for the current session. Used to resolve ancillary stores
    /// for cross-store enrichment. Implementations should return the DI container
    /// scoped to this session; the default is null.
    /// </summary>
    IServiceProvider? Services => null;
}