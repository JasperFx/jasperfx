using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.Events.InMemory;

/// <summary>
/// The one database of the in-memory prototyping store (jasperfx#985) -- what
/// <see cref="InMemoryDocumentStore.AllDatabases"/> returns, so a store-agnostic caller can open a session on it
/// and wait on its projections.
/// </summary>
/// <remarks>
/// Projections run inline inside the commit, so there are no async shards to report progress for and the data
/// is never stale.
/// </remarks>
public sealed class InMemoryEventDatabase : IEventDatabase
{
    internal const string Name = "main";

    private readonly InMemoryDocumentStore _store;

    internal InMemoryEventDatabase(InMemoryDocumentStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public string Identifier => Name;

    /// <inheritdoc />
    public Uri DatabaseUri { get; } = new("inmemory://main");

    /// <inheritdoc />
    public string StorageIdentifier => "inmemory";

    /// <inheritdoc />
    public ShardStateTracker Tracker { get; } = new(NullLogger.Instance) { DatabaseIdentifier = Name };

    /// <summary>Inline projections are written in the commit, so there is nothing to wait for.</summary>
    public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => Task.CompletedTask;

    /// <summary>There is no storage to create.</summary>
    public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token) => Task.CompletedTask;

    /// <summary>Zero for any name: there are no async shards on the prototyping store.</summary>
    public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
        => Task.FromResult(0L);

    /// <summary>Empty: there are no async shards on the prototyping store.</summary>
    public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<ShardState>>([]);

    /// <inheritdoc />
    public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
        => Task.FromResult(_store.HighestEventSequence());

    /// <inheritdoc />
    public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
        => Task.FromResult(_store.EventSequenceFloorAt(timestamp));

    /// <inheritdoc />
    public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
        => throw InMemoryEventRegistry.NotSupported("Dead letter events");
}
