namespace JasperFx.Descriptors;

/// <summary>
/// Snapshot of a projection's current status, including all of its
/// shards. Returned as a list by the explorer's <c>GetProjectionStatusesAsync</c>
/// to populate the projections page; live updates flow over the existing
/// <c>ShardStatesChanged</c> event so monitoring tools can subscribe
/// without polling.
/// </summary>
/// <param name="ProjectionName">Configured name of the projection.</param>
/// <param name="Lifecycle">String form of the projection's lifecycle (Inline / Async / Live).</param>
/// <param name="Shards">Per-shard status records for this projection.</param>
public sealed record ProjectionStatus(
    string ProjectionName,
    string Lifecycle,
    IReadOnlyList<ShardStatus> Shards);

/// <summary>
/// Per-shard status snapshot inside a <see cref="ProjectionStatus"/>.
/// Tracks how far the shard has processed and surfaces any latched error
/// so operators can spot stuck shards from the explorer view.
/// </summary>
/// <param name="ShardName">Compound shard name (projection name + group key).</param>
/// <param name="State">String form of the shard's runtime state (e.g. <c>"Running"</c>, <c>"Paused"</c>, <c>"Failed"</c>).</param>
/// <param name="ProcessedSequence">Highest event sequence the shard has consumed.</param>
/// <param name="EventStoreSequence">Current head sequence of the underlying event store at the moment of the snapshot.</param>
/// <param name="Error">Latched error message when the shard is in a failed state; <see langword="null"/> otherwise.</param>
public sealed record ShardStatus(
    string ShardName,
    string State,
    long ProcessedSequence,
    long EventStoreSequence,
    string? Error);
