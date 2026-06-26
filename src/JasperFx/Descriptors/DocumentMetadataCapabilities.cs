namespace JasperFx.Descriptors;

/// <summary>
/// Store-agnostic description of <em>which</em> document metadata an event/document
/// store actually captures. Surfaced on <see cref="DocumentStoreUsage.DocumentMetadata"/>
/// so store-aware consumers (notably CritterWatch) can gate faceted document-query
/// options by what the store really persists, instead of sniffing engine-specific
/// configuration.
/// </summary>
/// <remarks>
/// <para>
/// Parallel to <c>JasperFx.Events.Descriptors.EventMetadataCapabilities</c> but a
/// <strong>separate</strong> block: even within one engine the metadata captured
/// for documents can differ from what is captured for events, so a single block
/// cannot represent both (see jasperfx#475).
/// </para>
/// <para>
/// <see cref="Version"/>, <see cref="LastModified"/>, <see cref="TenantId"/> and
/// <see cref="SoftDelete"/> are the common document-metadata columns and default
/// to <see langword="true"/>; the implementing store overrides any it does not
/// capture. The optional, opt-in columns (<see cref="CorrelationId"/>,
/// <see cref="CausationId"/>, <see cref="LastModifiedBy"/>) default to
/// <see langword="false"/> and are turned on only where the document config
/// enables them.
/// </para>
/// </remarks>
public class DocumentMetadataCapabilities
{
    /// <summary>
    /// Engine discriminator for the store that produced this descriptor
    /// (e.g. <c>"Marten"</c>, <c>"Polecat"</c>). Lets consumers branch on the
    /// concrete engine where the uniform flags are not enough.
    /// </summary>
    public string StoreType { get; set; } = "";

    /// <summary>
    /// Documents carry a queryable version column. Common metadata; defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool Version { get; set; } = true;

    /// <summary>
    /// Documents carry a queryable last-modified timestamp. Common metadata;
    /// defaults to <see langword="true"/>.
    /// </summary>
    public bool LastModified { get; set; } = true;

    /// <summary>
    /// Documents carry a queryable tenant-id column. Common metadata; defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool TenantId { get; set; } = true;

    /// <summary>
    /// Documents expose a soft-delete flag (and deletion timestamp) rather than
    /// being hard-deleted. Common metadata; defaults to <see langword="true"/>.
    /// </summary>
    public bool SoftDelete { get; set; } = true;

    /// <summary>
    /// Documents capture a correlation-id column — only where the document
    /// metadata config enables it; defaults to <see langword="false"/>.
    /// </summary>
    public bool CorrelationId { get; set; }

    /// <summary>
    /// Documents capture a causation-id column — only where the document
    /// metadata config enables it; defaults to <see langword="false"/>.
    /// </summary>
    public bool CausationId { get; set; }

    /// <summary>
    /// Documents capture a last-modified-by (user name) column — only where the
    /// document metadata config enables it; defaults to <see langword="false"/>.
    /// </summary>
    public bool LastModifiedBy { get; set; }
}
