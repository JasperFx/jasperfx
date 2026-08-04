using JasperFx.Blocks;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Subscribes to a daemon's <see cref="ShardStateTracker"/> and persists the extended progression
/// telemetry (heartbeat, agent status, pause reason, running node) that the subscription agents
/// already compute in process, by driving <see cref="IEventDatabase.WriteExtendedProgressionAsync(System.Collections.Generic.IReadOnlyList{ShardState},System.Threading.CancellationToken)"/>.
/// This is the missing write half of extended progression tracking — the schema columns and the read
/// surface existed, but no daemon path ever persisted them (jasperfx#537, "built and never connected"
/// per #519).
///
/// <para>
/// Behavior:
/// <list type="bullet">
/// <item>Gated on <see cref="IEventStore.ExtendedProgressionEnabled"/>, read live per publication —
/// nothing is written (or even queued) for stores that have not opted in.</item>
/// <item>jasperfx#622: heartbeat/telemetry publications (<see cref="ShardAction.Updated"/>) are
/// DROPPED by default. The periodic per-shard beat had no reader anywhere — not in JasperFx, not
/// in Marten, and not in CritterWatch, which reads agent status and heartbeats off in-memory
/// objects and drops the persisted columns on the floor — while costing one pooled connection and
/// one transaction per database per node every 5 seconds. On a 512-shard-database deployment that
/// was ~37 connection acquisitions/sec/node to keep 6-12 rows current, and it made a production web
/// app unresponsive (marten#5167). Liveness is a node property and is tracked as one per node
/// upstream; <c>last_updated</c> plus the agent assignment grid reconstructs what any consumer
/// actually renders. Set <see cref="HeartbeatWriteInterval"/> (or
/// <c>DaemonSettings.ExtendedProgressionHeartbeatInterval</c>) to a positive value to restore the
/// old behavior — that is the compatibility hatch, not the recommended shape.</item>
/// <item>When periodic beats ARE enabled, they are coalesced per shard (latest state wins) and
/// flushed as ONE batch per <see cref="HeartbeatWriteInterval"/> for the whole database, ordered by
/// shard name. The connection rent rate is therefore constant per database instead of O(shards):
/// under per-tenant agent fan-out (agents = projections × tenants) the previous
/// one-connection-rent-per-shard-per-interval write path drove a sharded multi-tenant deployment
/// to its database server's connection ceiling (jasperfx#553). What a batch is NOT is one
/// transaction — see the one-row-per-transaction requirement on
/// <see cref="IEventDatabase.WriteExtendedProgressionAsync(System.Collections.Generic.IReadOnlyList{ShardState},System.Threading.CancellationToken)"/>,
/// which is what keeps a slow projection batch on one row from stalling every other shard's
/// telemetry behind it (marten#5167).</item>
/// <item>Agent status transitions (<see cref="ShardAction.Started"/>, <see cref="ShardAction.Paused"/>,
/// <see cref="ShardAction.Stopped"/>) flush immediately — a paused/stopped shard is exactly when the
/// persisted status matters most, and these writes are rare, so they keep the "durable across a
/// crash" story for the data where it means something. The pending heartbeat batch, if periodic
/// beats are enabled, rides along in the same write.</item>
/// <item>Writes are best-effort and serialized on a background block: a failed write is logged at
/// debug and can never fail or stall the shard, and a slow database can never back up the
/// tracker's publication loop.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ExtendedProgressionWriter : IObserver<ShardState>, IExclusiveTrackerObserver, IAsyncDisposable
{
    /// <summary>
    /// One writer per database is the design. See <see cref="IExclusiveTrackerObserver"/>.
    /// </summary>
    public const string ExclusiveRole = "extended progression writer";

    string IExclusiveTrackerObserver.Role => ExclusiveRole;

    private readonly IEventStore _store;
    private readonly IEventDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Block<ShardState[]> _block;

    // Only ever touched from the tracker's single publication consumer, so no synchronization needed
    private readonly Dictionary<string, ShardState> _pending = new();
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    public ExtendedProgressionWriter(IEventStore store, IEventDatabase database, TimeProvider timeProvider,
        ILogger logger)
    {
        _store = store;
        _database = database;
        _timeProvider = timeProvider;
        _logger = logger;

        _block = new Block<ShardState[]>(writeAsync);

        // Belt and braces: writeAsync already swallows its own failures, but a failure escaping the
        // block must still never take anything else down
        _block.OnError = (states, ex) =>
            _logger.LogDebug(ex, "Failed to persist extended progression for {Count} shard(s)", states.Length);
    }

    /// <summary>
    /// Spacing between two batched heartbeat/telemetry flushes for the database. All shard states
    /// that arrive within the interval are coalesced (latest state per shard) into the next flush,
    /// so no heartbeat is ever more than one interval stale. Status transitions always flush
    /// immediately, carrying any pending batch with them.
    ///
    /// <para>
    /// jasperfx#622: defaults to <see cref="TimeSpan.Zero"/> — periodic heartbeat writes are OFF, and
    /// only status transitions are persisted. Zero or negative disables them; any positive value
    /// restores the periodic beat at that cadence. Before #622 this was hardcoded to 5 seconds with
    /// no configuration path at all (the field was private on the daemon, reachable from no
    /// <c>DaemonSettings</c> knob), which is what made the cost impossible to opt out of.
    /// </para>
    /// </summary>
    public TimeSpan HeartbeatWriteInterval { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Whether this writer persists the periodic per-shard heartbeat at all. False by default; see
    /// <see cref="HeartbeatWriteInterval"/>.
    /// </summary>
    public bool PeriodicHeartbeatsEnabled => HeartbeatWriteInterval > TimeSpan.Zero;

    public void OnNext(ShardState value)
    {
        if (!_store.ExtendedProgressionEnabled) return;

        // Only real projection/subscription shards have a progression row to decorate
        if (value.ShardName == ShardState.HighWaterMark || value.ShardName == ShardState.AllProjections) return;

        // Plain progress publications (e.g. rebuild range completions) carry no agent telemetry
        if (value.AgentStatus == null && value.LastHeartbeat == null) return;

        var isTransition = value.Action is ShardAction.Started or ShardAction.Paused or ShardAction.Stopped;

        // jasperfx#622: with the periodic beat off, a non-transition publication is dropped outright
        // rather than queued -- there is no later flush to carry it, and letting it ride along on the
        // next transition would write a stale heartbeat nobody reads.
        if (!isTransition && !PeriodicHeartbeatsEnabled) return;

        // Carry the assigned node through to the persisted running_on_node column when a
        // distribution layer (e.g. Wolverine-managed subscription distribution) stamped it
        if (value.RunningOnNode == null && value.AssignedNodeNumber != 0)
        {
            value.RunningOnNode = value.AssignedNodeNumber;
        }

        // Latest state per shard wins; a transition that lands on top of a queued heartbeat
        // simply replaces it
        _pending[value.ShardName] = value;

        var now = _timeProvider.GetUtcNow();

        if (isTransition || now - _lastFlush >= HeartbeatWriteInterval)
        {
            flush(now);
        }
    }

    private void flush(DateTimeOffset now)
    {
        if (_pending.Count == 0) return;

        // Ordered by shard name because the batch is a lock-acquisition order (marten#5167). A store
        // writes these one row per transaction, so the batch never holds more than one row lock at a
        // time -- but two writers racing over the same rows (the tracker is per-database and shared, and
        // building a daemon does not go through a cache) would still be free to take their locks in
        // opposite orders if the batch order were the dictionary's. Sorting removes that hazard for
        // every store at zero cost; nothing downstream may reorder it.
        var batch = _pending.Values.OrderBy(x => x.ShardName, StringComparer.Ordinal).ToArray();
        _pending.Clear();
        _lastFlush = now;

        _block.Post(batch);
    }

    private async Task writeAsync(ShardState[] states, CancellationToken token)
    {
        try
        {
            await _database.WriteExtendedProgressionAsync(states, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Best-effort telemetry: a failed extended-progression write must NEVER fail or
            // stall the shards
            _logger.LogDebug(e, "Failed to persist extended progression for {Count} shard(s) on database {Database}",
                states.Length, _database.Identifier);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public async ValueTask DisposeAsync()
    {
        // Push any coalesced-but-unflushed states (e.g. the Stopped states published during shutdown
        // arrive flushed already, but a trailing heartbeat may not be), then AWAIT the drain. Returning
        // before the queued writes complete let a Stopped write land in the background *after* the daemon
        // was reported stopped and clobber a later deliberate write to the same progression row
        // (jasperfx#557). WaitForCompletionAsync completes the block, then awaits its in-flight writes.
        flush(_timeProvider.GetUtcNow());
        await _block.WaitForCompletionAsync().ConfigureAwait(false);
    }
}
