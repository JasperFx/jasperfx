namespace JasperFx.Events.Daemon;

/// <summary>
/// jasperfx#565: the identity of the single event that broke a shard, lifted off an exception into a
/// plain, serializable value. External supervisors (Wolverine's assignment plane, CritterWatch) ship
/// this over a wire and render it in a UI, which an <see cref="Exception"/> can't reliably do — and an
/// exception also drags along whatever object graph its data referenced.
///
/// <para>
/// Every member except <see cref="Sequence"/> is nullable: a serialization failure is raised while
/// reading a row, before there is an <see cref="IEvent"/>, so it may know only the sequence and the
/// stored type alias. See <see cref="IEventFailureContext"/>.
/// </para>
///
/// <para>
/// <see cref="Sequence"/> plus the owning shard is also the correlation key to a
/// <see cref="DeadLetterEvent"/> row: nothing is written to the dead-letter table when a shard PAUSES
/// (a paused event was not skipped), but if the same event is later skipped —
/// <see cref="ErrorHandlingOptions.SkipApplyErrors"/> and friends — its dead letter carries the same
/// projection name, shard key, sequence and tenant, so a consumer can line the two up.
/// </para>
/// </summary>
public record EventFailureDetails
{
    /// <summary>
    /// Store-wide sequence number of the failing event.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>
    /// The event store's type alias for the failing event (e.g. <c>trip_started</c>), if known.
    /// </summary>
    public string? EventTypeName { get; init; }

    /// <summary>
    /// Unique id of the failing event, if known.
    /// </summary>
    public Guid? EventId { get; init; }

    /// <summary>
    /// Stream id of the failing event for Guid-identified streams, if known.
    /// </summary>
    public Guid? StreamId { get; init; }

    /// <summary>
    /// Stream key of the failing event for string-identified streams, if known.
    /// </summary>
    public string? StreamKey { get; init; }

    /// <summary>
    /// Tenant of the failing event, if known.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Version of the failing event within its stream, if known.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>
    /// Lift the failing event's identity off an exception that knows it.
    /// </summary>
    public static EventFailureDetails From(IEventFailureContext context)
    {
        return new EventFailureDetails
        {
            Sequence = context.Sequence,
            EventTypeName = context.EventTypeName,
            EventId = context.EventId,
            StreamId = context.StreamId,
            StreamKey = context.StreamKey,
            TenantId = context.TenantId,
            Version = context.Version
        };
    }

    /// <summary>
    /// Lift the identity off a fully materialized event.
    /// </summary>
    public static EventFailureDetails From(IEvent @event)
    {
        return new EventFailureDetails
        {
            Sequence = @event.Sequence,
            EventTypeName = @event.EventTypeName,
            EventId = @event.Id,
            // Guid.Empty is how a string-keyed stream reports "no Guid id", and vice versa. Normalize
            // both to null so a consumer never renders a meaningless Guid.Empty or an empty key.
            StreamId = @event.StreamId == Guid.Empty ? null : @event.StreamId,
            StreamKey = string.IsNullOrEmpty(@event.StreamKey) ? null : @event.StreamKey,
            TenantId = @event.TenantId,
            Version = @event.Version
        };
    }

    public override string ToString()
    {
        var stream = StreamId?.ToString() ?? StreamKey;
        return
            $"event #{Sequence}{(EventTypeName == null ? "" : $" ({EventTypeName})")}{(stream == null ? "" : $" on stream {stream}")}{(TenantId == null ? "" : $" for tenant '{TenantId}'")}";
    }
}
