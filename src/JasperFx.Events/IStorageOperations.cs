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

    /// <summary>
    /// marten#6004: where the events this session committed are, so the caller can wait for asynchronous
    /// projections to process them with
    /// <see cref="IEventStore.WaitForProjectionsAsync(EventStorePosition, IReadOnlyList{Type}, TimeSpan, CancellationToken)" />.
    /// The highest event sequence any <c>SaveChangesAsync</c> on this session appended, with its database and tenant.
    /// Null until a commit appends events, and null from a store that does not record it, in which case there is
    /// no position to wait on.
    /// </summary>
    EventStorePosition? LastAppendedSequence => null;
}