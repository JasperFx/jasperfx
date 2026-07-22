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
    ///     Fetch the highest assigned event sequence number for a single tenant partition — the "head"
    ///     an explorer subtracts projection progress from to render lag. A null <paramref name="tenantId" />
    ///     is store-global and delegates to the tenant-less overload (today's behavior). Under
    ///     <c>UseTenantPartitionedEvents</c> each tenant draws its own sequence, so the store-global head
    ///     is not a meaningful lag baseline for any single tenant; event stores that implement per-tenant
    ///     partitioning override this. The default throws for a non-null tenant. See jasperfx#503.
    /// </summary>
    /// <param name="tenantId">Tenant partition to scope the head sequence to. Null means store-global.</param>
    /// <param name="token"></param>
    Task<long> FetchHighestEventSequenceNumber(string? tenantId, CancellationToken token)
        => tenantId == null
            ? FetchHighestEventSequenceNumber(token)
            : throw new NotSupportedException(
                "Per-tenant FetchHighestEventSequenceNumber is not implemented on this IEventDatabase. Use an event store that implements per-tenant partitioning.");


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
    ///     Delete a single projection-progression row by its raw shard-name
    ///     <see cref="ShardName.Identity" /> (e.g. <c>claim_lines:V9:All</c>), independent of whether a
    ///     matching projection is still registered in the running application. This is the store-agnostic,
    ///     DI-reachable companion to the read side (<see cref="AllProjectionProgress(CancellationToken)" />,
    ///     <see cref="ProjectionProgressFor" />) and is the eject path for an <em>orphaned</em> shard — a
    ///     renamed/versioned/removed projection, or a shard left behind by a topology change — whose row can
    ///     no longer be dropped through the registered-projection-keyed
    ///     <c>IEventStore&lt;,&gt;.DeleteProjectionProgressAsync</c> (which resolves its argument against the
    ///     registered projections and so cannot target an unregistered identity).
    ///     The match is on the exact shard identity, <em>not</em> a projection name or prefix. The default
    ///     implementation throws <see cref="NotSupportedException" />; event stores (Marten, Polecat) override
    ///     this to delete the row directly, bypassing any registered-projection lookup. See jasperfx#473.
    /// </summary>
    /// <param name="shardIdentity">The raw <see cref="ShardName.Identity" /> of the progression row to delete.</param>
    /// <param name="token"></param>
    Task DeleteProjectionProgressByShardNameAsync(string shardIdentity, CancellationToken token = default)
        => throw new NotSupportedException(
            "DeleteProjectionProgressByShardNameAsync is not implemented on this IEventDatabase. Use an event store (Marten or Polecat) that supports deleting a projection-progression row by its raw shard identity.");

    /// <summary>
    ///     Persist the extended progression telemetry for a single projection/subscription shard —
    ///     heartbeat, agent status, pause reason, running node — onto the store's progression row for
    ///     <see cref="ShardState.ShardName" /> (the raw shard identity). This is the write half of the
    ///     extended progression tracking feature (<see cref="IEventStoreInstrumentation.ExtendedProgressionEnabled" />):
    ///     the daemon computes these values in process (agent status transitions, the ~10s heartbeat
    ///     timer) and drives this method through <see cref="Daemon.ExtendedProgressionWriter" />, so a
    ///     monitoring consumer polling the database (e.g. CritterWatch when the publishing node is down)
    ///     can see shard health without an in-process channel. See jasperfx#537.
    ///     <para>
    ///     Contract for implementations:
    ///     <list type="bullet">
    ///     <item>Best-effort telemetry — never throw for a missing row; a shard that has not yet
    ///     committed any progression simply has nowhere to record status, and the write should no-op.
    ///     Transient failures may throw; the caller logs at debug and never fails the shard.</item>
    ///     <item>Do NOT advance or regress <c>last_seq_id</c>-equivalent progression from this path.
    ///     Progression is owned by the projection batch commit; this write updates only the extended
    ///     telemetry columns, so it can never race a concurrent batch commit into losing progress.</item>
    ///     </list>
    ///     </para>
    ///     The default implementation is a graceful no-op so existing stores compile and degrade
    ///     silently until they implement the write (the established graceful-no-op pattern).
    /// </summary>
    /// <param name="state">
    ///     The published shard state carrying <see cref="ShardState.AgentStatus" />,
    ///     <see cref="ShardState.PauseReason" />, <see cref="ShardState.LastHeartbeat" /> and
    ///     <see cref="ShardState.RunningOnNode" /> for the shard named by
    ///     <see cref="ShardState.ShardName" />.
    /// </param>
    /// <param name="token"></param>
    Task WriteExtendedProgressionAsync(ShardState state, CancellationToken token = default)
        => Task.CompletedTask;

    /// <summary>
    ///     Persist the extended progression telemetry for a batch of shards in as few database
    ///     round-trips as the store can manage — ideally one. The <see cref="Daemon.ExtendedProgressionWriter" />
    ///     coalesces the heartbeat publications of every shard on a database into one batch per flush
    ///     interval and drives this overload, because the per-shard single-row write does not scale
    ///     under per-tenant agent fan-out: agents = projections × tenants, and one connection
    ///     rent + round-trip per agent per heartbeat interval drove a sharded multi-tenant deployment
    ///     to its database server's connection ceiling (jasperfx#553).
    ///     <para>
    ///     Same contract as the single-state overload: best-effort telemetry, never advance or
    ///     regress progression, missing rows no-op. The batch is at-most-one-state-per-shard
    ///     (the writer keeps only the latest state per shard between flushes).
    ///     </para>
    ///     The default implementation degrades to the single-state overload per shard so existing
    ///     stores keep working unchanged until they implement a true batched write.
    /// </summary>
    /// <param name="states">The latest published state per shard, at most one entry per shard name.</param>
    /// <param name="token"></param>
    async Task WriteExtendedProgressionAsync(IReadOnlyList<ShardState> states, CancellationToken token = default)
    {
        foreach (var state in states)
        {
            await WriteExtendedProgressionAsync(state, token).ConfigureAwait(false);
        }
    }

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

    /// <summary>
    ///     Read the current sequence + lifecycle state for a single (projection, tenant) cell directly
    ///     from the progression table, without spinning up an <see cref="Daemon.IProjectionDaemon" />.
    ///     Returns null when no row exists for the pair yet — e.g. the daemon has not yet observed this
    ///     projection for this tenant. A null <paramref name="tenantId" /> means store-global on a
    ///     non-tenanted store, or the default-tenant row on a tenanted store.
    ///     <para>
    ///     This is the targeted counterpart to <see cref="AllProjectionProgress(CancellationToken)" />,
    ///     which returns every row across every projection × tenant pair and leaves the caller to filter.
    ///     For a per-cell UI polling loop (e.g. 1Hz against a single visible batch) the targeted query is
    ///     materially cheaper.
    ///     </para>
    ///     The default implementation throws <see cref="NotSupportedException" /> rather than returning
    ///     null: null is the meaningful "no row yet" answer, so a store that simply has not implemented
    ///     this must not borrow it and report a live cell as absent. Event stores (Marten, Polecat)
    ///     override this against their progression table. See jasperfx#435.
    /// </summary>
    /// <param name="projectionName">Name of the projection whose cell to read.</param>
    /// <param name="tenantId">Tenant owning the cell. Null means store-global / default tenant.</param>
    /// <param name="token"></param>
    ValueTask<ProjectionProgressRow?> ReadProjectionProgressAsync(
        string projectionName,
        string? tenantId,
        CancellationToken token)
        => throw new NotSupportedException(
            "ReadProjectionProgressAsync is not implemented on this IEventDatabase. Use an event store (Marten or Polecat) that supports reading a single projection progression row.");

    /// <summary>
    ///     Exact per-cell progression read: look up the single progression row whose stored identity equals
    ///     <paramref name="name" />'s <see cref="ShardName.Identity" />. Unlike the
    ///     <see cref="ReadProjectionProgressAsync(string,string?,CancellationToken)" /> overload there is no
    ///     version/shard collapsing — the caller supplies the full <see cref="ShardName" /> it already holds
    ///     (for example a cell from <see cref="AllProjectionProgress(CancellationToken)" />), so a blue/green
    ///     deploy's versions, a sliced projection's shard keys, and per-tenant partitions each address their
    ///     own row unambiguously. A <see cref="ShardName.ShardKey" /> of <c>All</c> is the projection's global
    ///     cell, matching the store's "All means the whole projection" convention. Returns null when no row
    ///     exists for that identity yet.
    ///     <para>
    ///     Like the other overload the default implementation throws rather than returning null: null is the
    ///     meaningful "no row yet" answer and must not be borrowed by a store that has not implemented this.
    ///     Event stores (Marten, Polecat) override it against their progression table. See jasperfx#435.
    ///     </para>
    /// </summary>
    /// <param name="name">Full shard identity of the cell to read.</param>
    /// <param name="token"></param>
    ValueTask<ProjectionProgressRow?> ReadProjectionProgressAsync(
        ShardName name,
        CancellationToken token)
        => throw new NotSupportedException(
            "ReadProjectionProgressAsync is not implemented on this IEventDatabase. Use an event store (Marten or Polecat) that supports reading a single projection progression row.");
}