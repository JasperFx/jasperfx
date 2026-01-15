using JasperFx.Events.Daemon;

namespace JasperFx.Events;

public interface IStorageOperations : IAsyncDisposable
{
    Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
        CancellationToken cancellationToken);

    bool EnableSideEffectsOnInlineProjections { get; }

    ValueTask<IMessageSink> GetOrStartMessageSink();
}