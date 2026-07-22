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
/// <item>Heartbeat/telemetry publications (<see cref="ShardAction.Updated"/> — the ~10s heartbeat
/// timer ticks and the per-batch commit publications) are coalesced per shard (latest state wins)
/// and flushed as ONE batched database write per <see cref="HeartbeatWriteInterval"/> for the whole
/// database. The write rate is therefore constant per database instead of O(shards): under
/// per-tenant agent fan-out (agents = projections × tenants) the previous
/// one-connection-rent-per-shard-per-interval write path drove a sharded multi-tenant deployment
/// to its database server's connection ceiling (jasperfx#553).</item>
/// <item>Agent status transitions (<see cref="ShardAction.Started"/>, <see cref="ShardAction.Paused"/>,
/// <see cref="ShardAction.Stopped"/>) flush immediately — a paused/stopped shard is exactly when the
/// persisted status matters most. The pending heartbeat batch rides along in the same write.</item>
/// <item>Writes are best-effort and serialized on a background block: a failed write is logged at
/// debug and can never fail or stall the shard, and a slow database can never back up the
/// tracker's publication loop.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ExtendedProgressionWriter : IObserver<ShardState>, IAsyncDisposable
{
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
    /// so no heartbeat is ever more than one interval stale. Status transitions flush immediately,
    /// carrying any pending batch with them. Defaults to 5 seconds so every tick of the agents'
    /// 10 second heartbeat timer lands.
    /// </summary>
    public TimeSpan HeartbeatWriteInterval { get; set; } = TimeSpan.FromSeconds(5);

    public void OnNext(ShardState value)
    {
        if (!_store.ExtendedProgressionEnabled) return;

        // Only real projection/subscription shards have a progression row to decorate
        if (value.ShardName == ShardState.HighWaterMark || value.ShardName == ShardState.AllProjections) return;

        // Plain progress publications (e.g. rebuild range completions) carry no agent telemetry
        if (value.AgentStatus == null && value.LastHeartbeat == null) return;

        // Carry the assigned node through to the persisted running_on_node column when a
        // distribution layer (e.g. Wolverine-managed subscription distribution) stamped it
        if (value.RunningOnNode == null && value.AssignedNodeNumber != 0)
        {
            value.RunningOnNode = value.AssignedNodeNumber;
        }

        // Latest state per shard wins; a transition that lands on top of a queued heartbeat
        // simply replaces it
        _pending[value.ShardName] = value;

        var isTransition = value.Action is ShardAction.Started or ShardAction.Paused or ShardAction.Stopped;
        var now = _timeProvider.GetUtcNow();

        if (isTransition || now - _lastFlush >= HeartbeatWriteInterval)
        {
            flush(now);
        }
    }

    private void flush(DateTimeOffset now)
    {
        if (_pending.Count == 0) return;

        var batch = _pending.Values.ToArray();
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
