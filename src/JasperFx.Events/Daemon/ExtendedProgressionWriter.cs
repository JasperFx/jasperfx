using ImTools;
using JasperFx.Blocks;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Subscribes to a daemon's <see cref="ShardStateTracker"/> and persists the extended progression
/// telemetry (heartbeat, agent status, pause reason, running node) that the subscription agents
/// already compute in process, by driving <see cref="IEventDatabase.WriteExtendedProgressionAsync"/>.
/// This is the missing write half of extended progression tracking — the schema columns and the read
/// surface existed, but no daemon path ever persisted them (jasperfx#537, "built and never connected"
/// per #519).
///
/// <para>
/// Behavior:
/// <list type="bullet">
/// <item>Gated on <see cref="IEventStore.ExtendedProgressionEnabled"/>, read live per publication —
/// nothing is written (or even queued) for stores that have not opted in.</item>
/// <item>Agent status transitions (<see cref="ShardAction.Started"/>, <see cref="ShardAction.Paused"/>,
/// <see cref="ShardAction.Stopped"/>) are always written, immediately — a paused/stopped shard is
/// exactly when the persisted status matters most.</item>
/// <item><see cref="ShardAction.Updated"/> publications carrying agent telemetry (the ~10s heartbeat
/// timer ticks and the per-batch commit publications) are throttled to at most one write per
/// <see cref="HeartbeatWriteInterval"/> per shard, so a fast-committing shard does not turn every
/// batch into an extra progression-table write.</item>
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
    private readonly Block<ShardState> _block;

    // Only ever touched from the tracker's single publication consumer, so no synchronization needed
    private ImHashMap<string, DateTimeOffset> _lastWrites = ImHashMap<string, DateTimeOffset>.Empty;

    public ExtendedProgressionWriter(IEventStore store, IEventDatabase database, TimeProvider timeProvider,
        ILogger logger)
    {
        _store = store;
        _database = database;
        _timeProvider = timeProvider;
        _logger = logger;

        _block = new Block<ShardState>(writeAsync);

        // Belt and braces: writeAsync already swallows its own failures, but a failure escaping the
        // block must still never take anything else down
        _block.OnError = (state, ex) =>
            _logger.LogDebug(ex, "Failed to persist extended progression for shard {ShardName}", state.ShardName);
    }

    /// <summary>
    /// Minimum spacing between two persisted heartbeat/telemetry writes for the same shard on the
    /// non-transition (<see cref="ShardAction.Updated"/>) path. Status transitions are never throttled.
    /// Defaults to 5 seconds so every tick of the agents' 10 second heartbeat timer lands.
    /// </summary>
    public TimeSpan HeartbeatWriteInterval { get; set; } = TimeSpan.FromSeconds(5);

    public void OnNext(ShardState value)
    {
        if (!_store.ExtendedProgressionEnabled) return;

        // Only real projection/subscription shards have a progression row to decorate
        if (value.ShardName == ShardState.HighWaterMark || value.ShardName == ShardState.AllProjections) return;

        // Plain progress publications (e.g. rebuild range completions) carry no agent telemetry
        if (value.AgentStatus == null && value.LastHeartbeat == null) return;

        var isTransition = value.Action is ShardAction.Started or ShardAction.Paused or ShardAction.Stopped;
        var now = _timeProvider.GetUtcNow();

        if (!isTransition)
        {
            if (_lastWrites.TryFind(value.ShardName, out var last) && now - last < HeartbeatWriteInterval)
            {
                return;
            }
        }

        _lastWrites = _lastWrites.AddOrUpdate(value.ShardName, now);

        // Carry the assigned node through to the persisted running_on_node column when a
        // distribution layer (e.g. Wolverine-managed subscription distribution) stamped it
        if (value.RunningOnNode == null && value.AssignedNodeNumber != 0)
        {
            value.RunningOnNode = value.AssignedNodeNumber;
        }

        _block.Post(value);
    }

    private async Task writeAsync(ShardState state, CancellationToken token)
    {
        try
        {
            await _database.WriteExtendedProgressionAsync(state, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Best-effort telemetry: a failed extended-progression write must NEVER fail or
            // stall the shard
            _logger.LogDebug(e, "Failed to persist extended progression for shard {ShardName} on database {Database}",
                state.ShardName, _database.Identifier);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public ValueTask DisposeAsync()
    {
        // Lets any queued final writes (e.g. the Stopped state published during shutdown) drain
        // in the background rather than dropping them on the floor
        _block.Complete();
        return ValueTask.CompletedTask;
    }
}
