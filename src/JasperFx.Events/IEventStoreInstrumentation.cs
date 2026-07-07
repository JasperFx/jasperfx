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

    /// <summary>
    /// Optional observer invoked with the events appended in each successful unit-of-work commit
    /// (e.g. Marten/Polecat <c>SaveChanges</c>), letting storage-agnostic lifecycle tooling such as
    /// CritterWatch record runtime-observed "appends" edges. Each <see cref="IEvent" /> carries the
    /// data such an observer needs — event type, stream id/key, aggregate type, tenant id, and
    /// timestamp — so no store-specific projection is required.
    /// </summary>
    /// <remarks>
    /// A default no-op implementation keeps existing event stores source-compatible until they opt in;
    /// an implementing store stores the delegate and invokes it (best-effort, after commit) with the
    /// events it just appended. Combine observers with <c>+=</c>. See CritterWatch#500.
    /// </remarks>
    Action<IReadOnlyList<IEvent>>? AppendObserver
    {
        get => null;
        set { }
    }
}
