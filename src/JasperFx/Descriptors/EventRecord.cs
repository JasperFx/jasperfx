using System.Text.Json;

namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic representation of a single committed event suitable for
/// transport over the wire to monitoring tools. The body and metadata
/// arrive as <see cref="JsonElement"/> instances so the descriptor stays
/// untyped at the JasperFx layer — operators can render the raw JSON
/// without forcing the consumer to ship CLR event types.
/// </summary>
/// <param name="EventId">Globally unique identifier of the event.</param>
/// <param name="Sequence">Monotonic sequence number assigned by the event store across all streams.</param>
/// <param name="StreamVersion">Per-stream version number (1-based) at which this event was appended.</param>
/// <param name="StreamId">String form of the parent stream identifier.</param>
/// <param name="EventTypeName">Event-type alias used by the event store registry.</param>
/// <param name="Data">Serialized event body as JSON.</param>
/// <param name="Metadata">Optional serialized event metadata as JSON; <see langword="null"/> when the store does not expose metadata.</param>
/// <param name="Timestamp">Server-assigned timestamp at which the event was appended.</param>
/// <param name="TenantId">Tenant identifier when the event store is multi-tenanted; <see langword="null"/> otherwise.</param>
/// <param name="Tags">DCB tags carried by this event; <see langword="null"/> when the store does not expose tag data.</param>
public sealed record EventRecord(
    Guid EventId,
    long Sequence,
    long StreamVersion,
    string StreamId,
    string EventTypeName,
    JsonElement Data,
    JsonElement? Metadata,
    DateTimeOffset Timestamp,
    string? TenantId,
    IReadOnlyDictionary<string, string>? Tags);
