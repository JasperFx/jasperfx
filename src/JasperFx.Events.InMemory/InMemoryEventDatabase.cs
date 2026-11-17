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
/// Inline projections run inside the commit. Async projections report their progression here when a daemon is
/// running them (jasperfx#1201).
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

    /// <summary>
    /// Wait for every async shard to reach the highest event sequence. Inline projections are written in the
    /// commit, so with none registered there is nothing to wait for.
    /// </summary>
    public async Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
    {
        var shards = _store.AllShards().Select(x => x.Name.Identity).ToArray();
        if (shards.Length == 0) return;

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (true)
        {
            var highest = _store.HighestEventSequence();
            if (shards.All(x => _store.ProgressionFor(x) >= highest)) return;

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The async projections did not reach event sequence {highest} within {timeout}: " +
                    string.Join(", ", shards.Select(x => $"{x} at {_store.ProgressionFor(x)}")));
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>There is no storage to create.</summary>
    public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
        => Task.FromResult(_store.ProgressionFor(name.Identity));

    /// <inheritdoc />
    public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
        => Task.FromResult(_store.AllProgression());

    /// <inheritdoc />
    public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
        => Task.FromResult(_store.HighestEventSequence());

    /// <inheritdoc />
    public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
        => Task.FromResult(_store.EventSequenceFloorAt(timestamp));

    /// <inheritdoc />
    public Task<IReadOnlyList<PendingEventAge>> FetchFirstEventTimestampsAfterAsync(
        IReadOnlyList<(string? TenantId, long Sequence)> positions, CancellationToken token = default)
        => Task.FromResult(_store.FirstEventTimestampsAfter(positions));

    // jasperfx#809: projection start floors, keyed by projection name (no version) and tenant
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Projection, string Tenant), long> _floors = new();

    private static (string, string) floorKey(string projectionName, string? tenantId) => (projectionName, tenantId ?? "");

    /// <inheritdoc />
    public Task<long?> FetchProjectionFloorAsync(string projectionName, string? tenantId, CancellationToken token = default)
        => Task.FromResult<long?>(_floors.TryGetValue(floorKey(projectionName, tenantId), out var floor) ? floor : null);

    /// <inheritdoc />
    public Task<long> TrySetProjectionFloorAsync(string projectionName, string? tenantId, long floor,
        CancellationToken token = default)
        => Task.FromResult(_floors.GetOrAdd(floorKey(projectionName, tenantId), floor));

    /// <inheritdoc />
    public Task SetProjectionFloorAsync(string projectionName, string? tenantId, long? floor,
        CancellationToken token = default)
    {
        if (floor is { } value)
        {
            _floors[floorKey(projectionName, tenantId)] = value;
        }
        else
        {
            _floors.TryRemove(floorKey(projectionName, tenantId), out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
        => throw InMemoryEventRegistry.NotSupported("Dead letter events");
}
