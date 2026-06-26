namespace JasperFx.Events.Descriptors;

/// <summary>
/// Store-agnostic description of <em>which</em> event and stream metadata an event
/// store actually captures. Surfaced on <see cref="EventStoreUsage.EventMetadata"/>
/// so store-aware consumers (notably CritterWatch) can gate event/stream query
/// facets by what the store really persists, instead of sniffing engine-specific
/// configuration (Marten's <c>MetadataConfig</c>, Polecat's event options).
/// </summary>
/// <remarks>
/// <para>
/// The <strong>event</strong> metadata flags (<see cref="CorrelationId"/>,
/// <see cref="CausationId"/>, <see cref="Headers"/>, <see cref="UserName"/>) are
/// opt-in columns and so default to <see langword="false"/> — the implementing
/// store maps them from its own metadata configuration.
/// </para>
/// <para>
/// The <strong>stream</strong> metadata flags
/// (<see cref="StreamAggregateType"/>, <see cref="StreamVersion"/>,
/// <see cref="StreamTimestamps"/>, <see cref="TenantId"/>, <see cref="Archived"/>)
/// are effectively universal across both engines today and default to
/// <see langword="true"/>; the implementing store overrides any it does not
/// capture.
/// </para>
/// <para>
/// See jasperfx#475. <see cref="UserName"/> is currently Marten-only (Polecat
/// parity tracked at JasperFx/polecat#237) — the descriptor lets consumers gate
/// that facet cleanly until parity lands.
/// </para>
/// </remarks>
public class EventMetadataCapabilities
{
    /// <summary>
    /// Engine discriminator for the store that produced this descriptor
    /// (e.g. <c>"Marten"</c>, <c>"Polecat"</c>). Lets consumers branch on the
    /// concrete engine where the uniform flags are not enough.
    /// </summary>
    public string StoreType { get; set; } = "";

    /// <summary>
    /// The event store captures a per-event correlation id column.
    /// Maps from Marten's <c>MetadataConfig.CorrelationIdEnabled</c> /
    /// Polecat's <c>EnableCorrelationId</c>.
    /// </summary>
    public bool CorrelationId { get; set; }

    /// <summary>
    /// The event store captures a per-event causation id column.
    /// Maps from Marten's <c>MetadataConfig.CausationIdEnabled</c> /
    /// Polecat's <c>EnableCausationId</c>.
    /// </summary>
    public bool CausationId { get; set; }

    /// <summary>
    /// The event store captures a per-event headers column.
    /// Maps from Marten's <c>MetadataConfig.HeadersEnabled</c> /
    /// Polecat's <c>EnableHeaders</c>.
    /// </summary>
    public bool Headers { get; set; }

    /// <summary>
    /// The event store captures a per-event user-name column.
    /// Maps from Marten's <c>MetadataConfig.UserNameEnabled</c>; currently
    /// Marten-only (Polecat parity: JasperFx/polecat#237).
    /// </summary>
    public bool UserName { get; set; }

    /// <summary>
    /// The streams table exposes a queryable aggregate-type column. Universal
    /// across both engines today; defaults to <see langword="true"/>.
    /// </summary>
    public bool StreamAggregateType { get; set; } = true;

    /// <summary>
    /// The streams table exposes a queryable version column. Universal across
    /// both engines today; defaults to <see langword="true"/>.
    /// </summary>
    public bool StreamVersion { get; set; } = true;

    /// <summary>
    /// The streams table exposes queryable created/updated timestamp columns.
    /// Universal across both engines today; defaults to <see langword="true"/>.
    /// </summary>
    public bool StreamTimestamps { get; set; } = true;

    /// <summary>
    /// The streams table exposes a queryable tenant-id column. Universal across
    /// both engines today; defaults to <see langword="true"/>.
    /// </summary>
    public bool TenantId { get; set; } = true;

    /// <summary>
    /// The streams table exposes a queryable archived/soft-deleted flag.
    /// Universal across both engines today; defaults to <see langword="true"/>.
    /// </summary>
    public bool Archived { get; set; } = true;
}
