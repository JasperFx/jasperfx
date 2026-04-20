namespace JasperFx.Events;

/// <summary>
///     Per-message metadata carried through the projection side-effect publishing
///     pipeline (<see cref="IEventSlice{T}.PublishMessage(object, MessageMetadata)"/> →
///     <see cref="Projections.IProjectionBatch.PublishMessageAsync(object, MessageMetadata)"/> →
///     <see cref="IMessageSink.PublishAsync{T}(T, MessageMetadata)"/>). Implementers
///     consuming the sink (e.g. Wolverine) map these fields onto their native
///     delivery options so that user-authored projections can stamp a specific
///     <see cref="IMetadataContext.CorrelationId"/>, <see cref="IMetadataContext.CausationId"/>,
///     or custom headers on emitted commands.
///
///     This struct is a value type on purpose — it travels through the side-effect
///     pipeline by copy and should never allocate beyond the optional
///     <see cref="Headers"/> dictionary that only materializes when actually used.
/// </summary>
public struct MessageMetadata : IMetadataContext
{
    public MessageMetadata(string tenantId)
    {
        TenantId = tenantId;
    }

    /// <inheritdoc />
    public string TenantId { get; set; }

    /// <inheritdoc />
    public string? CausationId { get; set; }

    /// <inheritdoc />
    public string? CorrelationId { get; set; }

    /// <inheritdoc />
    [Obsolete("Prefer CurrentUserName")]
    public string? LastModifiedBy { get; set; }

    /// <inheritdoc />
    public string? CurrentUserName { get; set; }

    private Dictionary<string, object>? _headers;

    /// <inheritdoc />
    public Dictionary<string, object>? Headers => _headers;

    /// <summary>
    ///     Attach a header value. Lazy-allocates the underlying dictionary so
    ///     the metadata struct stays allocation-free when no headers are used.
    /// </summary>
    public MessageMetadata WithHeader(string key, object value)
    {
        _headers ??= new Dictionary<string, object>();
        _headers[key] = value;
        return this;
    }

    public bool CausationIdEnabled => CausationId is not null;
    public bool CorrelationIdEnabled => CorrelationId is not null;
    public bool HeadersEnabled => _headers is { Count: > 0 };
#pragma warning disable CS0618
    public bool UserNameEnabled => CurrentUserName is not null || LastModifiedBy is not null;
#pragma warning restore CS0618
}
