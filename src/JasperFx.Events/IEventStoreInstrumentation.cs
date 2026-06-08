namespace JasperFx.Events;

/// <summary>
/// Opt-in instrumentation toggles that an event store implementation exposes to monitoring
/// consumers such as CritterWatch. Implemented by Marten's <c>EventGraph</c> and Polecat's
/// equivalent options so that storage-agnostic tooling can enable the same monitoring UX across
/// stores without referencing concrete store types. See jasperfx#424.
/// </summary>
public interface IEventStoreInstrumentation
{
    /// <summary>
    /// When true, the event store extends its progression-tracking schema with CritterWatch-facing
    /// columns (heartbeat, agent_status, pause_reason, running_on_node, warning_behind_threshold,
    /// critical_behind_threshold) and the async daemon writes them from runtime state. The
    /// shard-state selector reads them back into <see cref="JasperFx.Events.Projections.ShardState" />.
    /// Defaults to false; consumers opt in.
    /// </summary>
    bool ExtendedProgressionEnabled { get; set; }
}
