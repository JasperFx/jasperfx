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

    // jasperfx#1215 / #1222: event-range marks, keyed by a string and the tenant. Like the floors, they outlive Clear()
    // and rebuild teardown.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Key, string Tenant), long> _marks = new();

    private static (string, string) markKey(string key, string? tenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return (key, tenantId ?? "");
    }

    /// <inheritdoc />
    public Task<long?> FetchEventRangeMarkAsync(string key, string? tenantId, CancellationToken token = default)
        => Task.FromResult<long?>(_marks.TryGetValue(markKey(key, tenantId), out var mark) ? mark : null);

    /// <inheritdoc />
    public Task<long> TrySetEventRangeMarkAsync(string key, string? tenantId, long mark,
        CancellationToken token = default)
        => Task.FromResult(_marks.GetOrAdd(markKey(key, tenantId), mark));

    /// <inheritdoc />
    public Task SetEventRangeMarkAsync(string key, string? tenantId, long? mark, CancellationToken token = default)
    {
        if (mark is { } value)
        {
            _marks[markKey(key, tenantId)] = value;
        }
        else
        {
            _marks.TryRemove(markKey(key, tenantId), out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
    {
        _store.StoreDeadLetter(deadLetterEvent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<long> CountDeadLetterEventsAsync(ShardName shard, CancellationToken token = default)
        => Task.FromResult((long)_store.DeadLetters(shard, null).Count);

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterEvent>> QueryDeadLetterEventsAsync(ShardName shard, string? tenantId,
        int offset, int limit, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<DeadLetterEvent>>(_store.DeadLetters(shard, tenantId).Skip(offset)
            .Take(limit).ToList());

    // ---- marten#6003: parked identities ----

    /// <inheritdoc />
    public Task<IReadOnlyList<ParkedIdentity>> FetchParkedIdentitiesAsync(ParkedIdentityQuery query,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = _store.ParkedRows(x =>
            (query.ProjectionName == null || x.ProjectionName == query.ProjectionName) &&
            (query.ProjectionVersion == null || x.ProjectionVersion == query.ProjectionVersion) &&
            (query.ShardKey == null || x.ShardKey == query.ShardKey) &&
            (query.TenantId == null || x.TenantId == query.TenantId));

        return Task.FromResult<IReadOnlyList<ParkedIdentity>>(rows.Take(Math.Max(query.Limit, 0)).ToList());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ParkedShardCount>> FetchParkedCountsAsync(string? tenantId,
        CancellationToken token = default)
    {
        var counts = _store.ParkedRows(x => tenantId == null || x.TenantId == tenantId)
            .GroupBy(x => (x.ProjectionName, x.ProjectionVersion, x.ShardKey, x.TenantId))
            .Select(x => new ParkedShardCount(x.Key.ProjectionName, x.Key.ProjectionVersion, x.Key.ShardKey,
                x.Key.TenantId, x.Count()))
            .ToList();

        return Task.FromResult<IReadOnlyList<ParkedShardCount>>(counts);
    }

    /// <inheritdoc />
    public Task<int> RequestParkedActionAsync(ParkedActionRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _store.RequestParkedActionAsync(request, token);
    }

    // How often the daemon read the parked rows, for the tests that pin "nothing parked costs nothing"
    internal int ParkedLoads;
    internal int RequestedActionLoads;

    /// <inheritdoc />
    public Task<IReadOnlyList<ParkedIdentity>> LoadParkedIdentitiesAsync(ShardName shard,
        CancellationToken token = default)
    {
        Interlocked.Increment(ref ParkedLoads);
        return Task.FromResult(_store.ParkedRows(x => matches(x, shard)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ParkedIdentity>> LoadRequestedParkedActionsAsync(ShardName shard,
        CancellationToken token = default)
    {
        Interlocked.Increment(ref RequestedActionLoads);
        return Task.FromResult<IReadOnlyList<ParkedIdentity>>(_store
            .ParkedRows(x => matches(x, shard) && x.RequestedAction.HasValue)
            .OrderBy(x => x.RequestedAt)
            .ToList());
    }

    /// <inheritdoc />
    public Task RecordParkedReplayFailureAsync(ParkedIdentityKey key, DateTimeOffset attemptedAt, string error,
        CancellationToken token = default)
        => _store.RecordParkedReplayFailureAsync(key, attemptedAt, error, token);

    // One shard's rows: its name, version and key, and its tenant when the shard is a tenant's own
    private static bool matches(ParkedIdentity row, ShardName shard)
        => row.ProjectionName == shard.Name && row.ProjectionVersion == shard.Version &&
           row.ShardKey == shard.ShardKey && (shard.TenantId == null || row.TenantId == shard.TenantId);
}
