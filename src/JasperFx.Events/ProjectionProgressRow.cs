namespace JasperFx.Events;

/// <summary>
/// Point in time state of a single (projection, tenant) progression cell, read straight from the
/// event store's progression table. This is the targeted, per-cell counterpart to
/// <see cref="IEventDatabase.AllProjectionProgress" /> — it exists so a monitoring tool polling one
/// visible cell does not have to fetch and filter every projection × tenant row on each tick.
/// See jasperfx#435.
/// </summary>
/// <param name="ProjectionName">Name of the projection this cell tracks.</param>
/// <param name="TenantId">
/// Tenant the cell belongs to. Null means store-global on a non-tenanted store, or the
/// default-tenant row on a tenanted store.
/// </param>
/// <param name="Sequence">Event sequence number this cell has processed through.</param>
/// <param name="AgentStatus">
/// Lifecycle state of the agent driving this cell, or null when the store does not persist one.
/// Left as a string rather than the <see cref="JasperFx.AgentStatus" /> enum on purpose: this is a
/// diagnostic read of whatever the store persisted, and a store may report a state outside the
/// enum's Running/Stopped/Paused.
/// <para>
/// Nullable because agent state is only persisted where a store both models the column and writes
/// it — a store with nothing to report must be able to say so rather than invent a value
/// (jasperfx#435). Marten and Polecat do populate it, via
/// <see cref="JasperFx.Events.Daemon.ExtendedProgressionWriter" />, but only when
/// <c>EnableExtendedProgressionTracking</c> is on; with it off the column is not even selected and
/// this reads NULL.
/// </para>
/// </param>
/// <param name="LastHeartbeat">
/// Timestamp the agent driving this cell last persisted telemetry; null when the store does not
/// track a heartbeat for it.
/// <para>
/// ⚠️ <b>Not a liveness signal.</b> Since jasperfx#622 the periodic per-shard beat is OFF by default
/// (<see cref="JasperFx.Events.Daemon.IReadOnlyDaemonSettings.ExtendedProgressionHeartbeatInterval" />),
/// so <see cref="JasperFx.Events.Daemon.ExtendedProgressionWriter" /> persists this only on a
/// Started / Paused / Stopped transition. On a healthy long-running agent it therefore freezes at
/// the timestamp of the last transition and ages without bound — a monitor that thresholds
/// <c>now - LastHeartbeat</c> will report every shard as dead shortly after startup. Take liveness
/// from the in-memory <see cref="JasperFx.Events.Projections.ShardState" /> stream instead (an
/// <c>IObserver&lt;ShardState&gt;</c> on the running daemon, which still beats every 10s), or set a
/// positive <c>ExtendedProgressionHeartbeatInterval</c> and accept the write cost jasperfx#622 and
/// marten#5167 removed.
/// </para>
/// </param>
public record ProjectionProgressRow(
    string ProjectionName,
    string? TenantId,
    long Sequence,
    string? AgentStatus,
    DateTimeOffset? LastHeartbeat);
