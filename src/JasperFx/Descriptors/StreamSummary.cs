namespace JasperFx.Descriptors;

/// <summary>
/// Lightweight summary record describing a single event stream for the
/// event store explorer. Carries just enough data to populate a list view
/// (id, type, version, timestamps, tenant) without pulling the events
/// themselves.
/// </summary>
/// <param name="StreamId">String form of the stream identifier (Guid or string key).</param>
/// <param name="StreamType">Optional aggregate / stream type alias when the underlying store knows it.</param>
/// <param name="Version">Current stream version (number of committed events).</param>
/// <param name="CreatedAt">Timestamp of the first event appended to this stream.</param>
/// <param name="LastUpdatedAt">Timestamp of the most recent event appended to this stream.</param>
/// <param name="TenantId">Tenant identifier when the event store is multi-tenanted; <see langword="null"/> otherwise.</param>
public sealed record StreamSummary(
    string StreamId,
    string? StreamType,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    string? TenantId);
