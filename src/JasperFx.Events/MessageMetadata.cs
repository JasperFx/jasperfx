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

    /// <summary>
    ///     The metadata for a message caused by this one event: its tenant and correlation id. Use it
    ///     when a slice spans several flows, where the events don't agree and
    ///     <c>slice.PublishMessage(message)</c> has no single value it could carry:
    ///     <code>
    ///     foreach (var e in slice.Events())
    ///     {
    ///         slice.PublishMessage(new ShipOrder(e.StreamId), MessageMetadata.From(e));
    ///     }
    ///     </code>
    /// </summary>
    /// <remarks>
    ///     The tenant is the event's rather than the slice's, because the message answers to that one
    ///     event. The two only differ where a slice crosses tenants on purpose, as a roll up or a global
    ///     stream does, and there the event's tenant is the one the flow belongs to. The causation id is
    ///     left out for the same reason <c>slice.PublishMessage(message)</c> leaves it out: a causation
    ///     header would overwrite the handler's own envelope id on the events it appends. Set
    ///     <see cref="CausationId"/> yourself if that is what you want.
    /// </remarks>
    public static MessageMetadata From(IEvent @event)
    {
        return new MessageMetadata(@event.TenantId) { CorrelationId = @event.CorrelationId };
    }

    /// <inheritdoc />
    public string TenantId { get; set; }

    /// <inheritdoc />
    public string? CausationId { get; set; }

    /// <inheritdoc />
    public string? CorrelationId { get; set; }

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
    public bool UserNameEnabled => CurrentUserName is not null;
}
