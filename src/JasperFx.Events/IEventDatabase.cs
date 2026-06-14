using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events;

public interface IEventDatabase 
{
    /// <summary>
    ///     Identifying name for infrastructure and logging
    /// </summary>
    string Identifier { get; }
    
    Uri DatabaseUri { get; }
    
    /// <summary>
    ///     *If* a projection daemon has been started for this database, this
    ///     is the ShardStateTracker for the running daemon. This is useful in testing
    ///     scenarios
    /// </summary>
    ShardStateTracker Tracker { get; }

    /// <summary>
    /// Store a dead letter event for a failed event in a projection or subscription
    /// </summary>
    /// <param name="storage">The parent event store</param>
    /// <param name="deadLetterEvent"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token);

    Task EnsureStorageExistsAsync(Type storageType, CancellationToken token);

    Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout);
    
    /// <summary>
    ///     Check the current progress of a single projection or projection shard
    /// </summary>
    /// <param name="tenantId">
    ///     Specify the database containing this tenant id. If omitted, this method uses the default
    ///     database
    /// </param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<long> ProjectionProgressFor(ShardName name,
        CancellationToken token = default);

    /// <summary>
    /// Find the position of the event store sequence just below the supplied timestamp. Will
    /// return null if there are no events below that time threshold
    /// </summary>
    /// <param name="timestamp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token);

    /// <summary>
    /// Find the position of the event store sequence just below the supplied timestamp for a single
    /// tenant partition. A null <paramref name="tenantId" /> is store-global and delegates to the
    /// tenant-less overload (today's behavior). Event stores that implement per-tenant partitioning
    /// override this; the default throws for a non-null tenant. See jasperfx#407.
    /// </summary>
    Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, string? tenantId, CancellationToken token)
        => tenantId == null
            ? FindEventStoreFloorAtTimeAsync(timestamp, token)
            : throw new NotSupportedException(
                "Per-tenant FindEventStoreFloorAtTimeAsync is not implemented on this IEventDatabase. Use an event store that implements per-tenant partitioning.");

    string StorageIdentifier { get; }
    Task<long> FetchHighestEventSequenceNumber(CancellationToken token);
    
    /// <summary>
    ///     Check the current progress of all asynchronous projections
    /// </summary>
    /// <param name="token"></param>
    /// <param name="tenantId">
    ///     Specify the database containing this tenant id. If omitted, this method uses the default
    ///     database
    /// </param>
    /// <returns></returns>
    Task<IReadOnlyList<ShardState>> AllProjectionProgress(
        CancellationToken token = default);

    /// <summary>
    ///     Check the current progress of all asynchronous projections for a single tenant partition.
    ///     A null <paramref name="tenantId" /> is store-global and delegates to the tenant-less overload
    ///     (today's behavior). Event stores that implement per-tenant partitioning override this; the
    ///     default throws for a non-null tenant. See jasperfx#407.
    /// </summary>
    /// <param name="tenantId">Tenant partition to scope progress to. Null means store-global.</param>
    /// <param name="token"></param>
    Task<IReadOnlyList<ShardState>> AllProjectionProgress(string? tenantId, CancellationToken token = default)
        => tenantId == null
            ? AllProjectionProgress(token)
            : throw new NotSupportedException(
                "Per-tenant AllProjectionProgress is not implemented on this IEventDatabase. Use an event store that implements per-tenant partitioning.");

    /// <summary>
    ///     Count the stored dead letter events for a single projection/subscription shard. With
    ///     <c>SkipApplyErrors</c> on (the JasperFx.Events 2.0 default), a failed <c>Apply()</c> is
    ///     recorded as a <see cref="DeadLetterEvent" /> and the shard keeps advancing, so the
    ///     accumulation of these is the primary "this projection is unhealthy" signal.
    ///     The default implementation returns 0 as a stand-in; event stores that persist dead
    ///     letters should override this. See jasperfx#356.
    /// </summary>
    /// <param name="shard">The projection/subscription shard to count dead letters for.</param>
    /// <param name="token"></param>
    Task<long> CountDeadLetterEventsAsync(ShardName shard, CancellationToken token = default)
        => Task.FromResult(0L);

    /// <summary>
    ///     Fetch the stored dead letter event rows for a single projection/subscription shard — the
    ///     drill-in companion to <see cref="CountDeadLetterEventsAsync" />. Returns the most recent
    ///     failures first (by event sequence), paged via <paramref name="offset" /> /
    ///     <paramref name="limit" />. A null <paramref name="tenantId" /> spans every tenant sharing the
    ///     database; under <c>UseTenantPartitionedEvents</c> pass a tenant to scope to one partition (the
    ///     dead-letter table stays store-global but each row records the failing event's tenant).
    ///     The default implementation returns an empty list as a stand-in; event stores that persist
    ///     dead letters should override this. See CritterWatch#369.
    /// </summary>
    /// <param name="shard">The projection/subscription shard whose dead-letter rows to fetch.</param>
    /// <param name="tenantId">Tenant partition to scope to. Null spans the whole database.</param>
    /// <param name="offset">Number of rows to skip (paging).</param>
    /// <param name="limit">Maximum number of rows to return (paging).</param>
    /// <param name="token"></param>
    Task<IReadOnlyList<DeadLetterEvent>> QueryDeadLetterEventsAsync(ShardName shard, string? tenantId,
        int offset, int limit, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<DeadLetterEvent>>([]);

    /// <summary>
    ///     Fetch the stored dead letter event counts for this database, one row per shard
    ///     (<see cref="DeadLetterShardCount.ProjectionName" /> + <see cref="DeadLetterShardCount.ShardKey" />).
    ///     Mirrors the "give me every row" shape of <see cref="AllProjectionProgress(CancellationToken)" />.
    ///     The default implementation returns an empty list as a stand-in; event stores that
    ///     persist dead letters should override this. See jasperfx#356.
    /// </summary>
    /// <param name="token"></param>
    Task<IReadOnlyList<DeadLetterShardCount>> FetchDeadLetterCountsAsync(CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<DeadLetterShardCount>>([]);

    /// <summary>
    ///     Fetch the stored dead letter event counts for a single tenant partition, one row per shard.
    ///     A null <paramref name="tenantId" /> is store-global and delegates to the tenant-less overload
    ///     (today's behavior). Under <c>UseTenantPartitionedEvents</c> the same shard accumulates dead
    ///     letters per tenant; event stores that implement per-tenant partitioning override this to group
    ///     by tenant and stamp <see cref="DeadLetterShardCount.TenantId" /> so a consumer keying by
    ///     <c>{ProjectionName}:{ShardKey}</c> no longer collapses the counts across tenants. The default
    ///     throws for a non-null tenant. See jasperfx#450.
    /// </summary>
    /// <param name="tenantId">Tenant partition to scope counts to. Null means store-global.</param>
    /// <param name="token"></param>
    Task<IReadOnlyList<DeadLetterShardCount>> FetchDeadLetterCountsAsync(string? tenantId,
        CancellationToken token = default)
        => tenantId == null
            ? FetchDeadLetterCountsAsync(token)
            : throw new NotSupportedException(
                "Per-tenant FetchDeadLetterCountsAsync is not implemented on this IEventDatabase. Use an event store that implements per-tenant partitioning.");
}