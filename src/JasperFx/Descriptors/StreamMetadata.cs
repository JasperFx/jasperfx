namespace JasperFx.Descriptors;

/// <summary>
/// Full diagnostic metadata for a single event stream. Returned by the
/// event store explorer when an operator drills into a specific stream;
/// the extra fields beyond <see cref="StreamSummary"/> (snapshots,
/// archive flag, tags) help diagnose lifecycle and DCB-tagging issues.
/// </summary>
/// <param name="StreamId">String form of the stream identifier.</param>
/// <param name="StreamType">Optional aggregate / stream type alias.</param>
/// <param name="Version">Current stream version.</param>
/// <param name="CreatedAt">Timestamp of the first event in the stream.</param>
/// <param name="LastUpdatedAt">Timestamp of the most recent event in the stream.</param>
/// <param name="LastSnapshotAt">Timestamp of the most recent snapshot/compaction; <see langword="null"/> when the stream has never been snapshotted.</param>
/// <param name="LastSnapshotVersion">Stream version captured by the most recent snapshot; <see langword="null"/> when no snapshot exists.</param>
/// <param name="IsArchived">True when the stream has been archived (soft-deleted) by the underlying store.</param>
/// <param name="TenantId">Tenant identifier when the event store is multi-tenanted; <see langword="null"/> otherwise.</param>
/// <param name="Tags">DCB-style tags currently associated with the stream.</param>
public sealed record StreamMetadata(
    string StreamId,
    string? StreamType,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    DateTimeOffset? LastSnapshotAt,
    long? LastSnapshotVersion,
    bool IsArchived,
    string? TenantId,
    IReadOnlyDictionary<string, string> Tags);
