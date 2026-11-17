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
    /// The position (database, tenant and sequence) of the highest event any <c>SaveChangesAsync</c> on this session
    /// appended. Null until a commit appends events, and null from a store that does not record it, in which case
    /// there is no position to wait on.
    /// </summary>
    /// <remarks>
    /// A default implementation, so a store compiled against JasperFx 3.5.0 still loads. Such a store implemented
    /// <see cref="LastAppendedSequence" /> instead and answers null here until it moves to this name.
    /// </remarks>
    EventStorePosition? LastAppendedPosition => null;

    /// <summary>
    /// The 3.5.0 name of <see cref="LastAppendedPosition" />, which it forwards to. It holds a full
    /// <see cref="EventStorePosition" />, not a sequence.
    /// </summary>
    [Obsolete("Use LastAppendedPosition. It holds the full EventStorePosition (database, tenant and sequence), not just a sequence.")]
    EventStorePosition? LastAppendedSequence => LastAppendedPosition;
}