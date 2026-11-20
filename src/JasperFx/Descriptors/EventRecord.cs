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
/// <param name="Tags">
/// DCB tags carried by this event; <see langword="null"/> when the store does not expose tag data. The key format is
/// documented on the <see cref="Tags"/> property.
/// </param>
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
    IReadOnlyDictionary<string, string>? Tags)
{
    /// <summary>
    /// DCB tags carried by this event; <see langword="null"/> when the store does not expose tag data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Key.</b> A store writes the registered tag type's CLR simple name (<c>"StudentId"</c>). A reader also
    /// accepts the registration's table suffix (<c>"student"</c>), so a store whose tag column is keyed by suffix
    /// can pass its keys through as they are. A key is resolved case-insensitively, the CLR name tried across every
    /// registration before the suffix, by <c>TagTypeRegistrationExtensions.FindByTagName</c> in JasperFx.Events:
    /// the same rule as the tag names of <c>EventQuery.TagValues</c> and <c>IEventStore.QueryByTagsAsync</c>. The
    /// CLR name is the one to write because it can't be shadowed: a suffix that happens to equal another tag type's
    /// name resolves to that other type.
    /// </para>
    /// <para>
    /// <b>Value.</b> The tag's simple value as text, in its canonical form
    /// (<c>TagValueStringifier.Stringify</c>): a Guid as <c>"d"</c>, a number in the invariant culture.
    /// </para>
    /// <para>
    /// The tag-copying <c>EventExtensions.WithRecordMetadata</c> overload in JasperFx.Events turns these back into
    /// the event's typed tags (jasperfx#1254).
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Tags { get; init; } = Tags;
}
