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
/// Lifecycle state of the agent driving this cell. Left as a string rather than the
/// <see cref="JasperFx.AgentStatus" /> enum on purpose: this is a diagnostic read of whatever the
/// store persisted, and a store may report a state outside the enum's Running/Stopped/Paused.
/// </param>
/// <param name="LastHeartbeat">
/// Timestamp the cell last reported progress; null when the store does not track a heartbeat for it.
/// </param>
public record ProjectionProgressRow(
    string ProjectionName,
    string? TenantId,
    long Sequence,
    string AgentStatus,
    DateTimeOffset? LastHeartbeat);
