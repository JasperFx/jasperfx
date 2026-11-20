using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

/// <summary>
/// marten#6003: how long a projection's slice index keeps its rows. See <see cref="AsyncOptions.UseSliceIndex()" />.
/// </summary>
public sealed record SliceIndexRetention
{
    private SliceIndexRetention(TimeSpan? window)
    {
        Window = window;
    }

    /// <summary>
    /// The default: keep the rows of the last <c>window</c> of events, plus every row a parked identity still needs.
    /// Enough to replay or discard a parked identity, not enough to rebuild any identity from the start.
    /// </summary>
    public static SliceIndexRetention ForParking(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The retention window has to be positive.");
        }

        return new SliceIndexRetention(window);
    }

    /// <summary>
    /// Keep every row. Needed to rebuild one identity from the start
    /// (<see cref="IProjectionDaemon.RebuildIdentityAsync(string, string?, object, CancellationToken)" />) and to step
    /// through an identity's whole history.
    /// </summary>
    public static SliceIndexRetention Full { get; } = new((TimeSpan?)null);

    /// <summary>The window <see cref="ForParking" /> keeps, or null for <see cref="Full" />.</summary>
    public TimeSpan? Window { get; }

    /// <summary>Is this <see cref="Full" />?</summary>
    public bool IsFull => Window == null;

    public override string ToString() => IsFull ? "Full" : $"ForParking({Window})";
}

/// <summary>
/// marten#6003: how a replay or per-identity rebuild of a projection with a slice index treats a custom grouping
/// (a lookup grouper or a custom slicer), whose answer can depend on data that changed since the event was applied.
/// </summary>
public enum SliceIndexGroupingReplay
{
    /// <summary>
    /// The default: the index is the grouping. The identity gets exactly the events the index says it got when the
    /// daemon applied them, and the custom grouping does not run again. Fan-out rules that run after grouping still run.
    /// </summary>
    AsRecorded,

    /// <summary>
    /// The opt-out: run the custom grouping again over the indexed events, and keep the events it gives the identity
    /// now. Use it when the grouping is a pure function of the events, or when you want today's answer.
    /// </summary>
    RerunGrouping
}

/// <summary>
/// marten#6003: a projection's slice index: for every event the daemon applies, which identities it went to. It makes
/// one identity's events findable for any multi-stream projection, so a failing identity can be parked and replayed.
/// Turn it on per projection with <see cref="AsyncOptions.UseSliceIndex()" />.
/// </summary>
public sealed class SliceIndexOptions
{
    /// <summary>How long rows are kept. Defaults to <see cref="SliceIndexRetention.ForParking" /> 7 days.</summary>
    public SliceIndexRetention Retention { get; set; } = SliceIndexRetention.ForParking(TimeSpan.FromDays(7));

    /// <summary>
    /// How a replay treats a custom grouping. Defaults to <see cref="SliceIndexGroupingReplay.AsRecorded" />. A
    /// projection without a custom grouping is sliced again either way, which gives the same answer.
    /// </summary>
    public SliceIndexGroupingReplay CustomGroupingReplay { get; set; } = SliceIndexGroupingReplay.AsRecorded;

    /// <summary>
    /// How often the daemon deletes rows that fell out of a <see cref="SliceIndexRetention.ForParking" /> window.
    /// Defaults to one hour. The delete runs on the shard's heartbeat on the node that runs the shard.
    /// </summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Keep every row (<see cref="SliceIndexRetention.Full" />).</summary>
    public SliceIndexOptions KeepEverything()
    {
        Retention = SliceIndexRetention.Full;
        return this;
    }

    /// <summary>
    /// The opt-out from replaying a custom grouping as recorded: run it again on replay. See
    /// <see cref="SliceIndexGroupingReplay.RerunGrouping" />.
    /// </summary>
    public SliceIndexOptions RerunCustomGroupingOnReplay()
    {
        CustomGroupingReplay = SliceIndexGroupingReplay.RerunGrouping;
        return this;
    }
}

/// <summary>
/// marten#6003: one row of a projection's slice index. The event with <see cref="Sequence" /> was applied to
/// <see cref="Identity" /> in <see cref="TenantId" />'s slice group.
/// </summary>
/// <param name="TenantId">The tenant of the slice group, never null.</param>
/// <param name="Identity">The identity, unwrapped to its simple value: a <see cref="Guid" />, <see cref="string" />,
/// <see cref="int" /> or <see cref="long" />.</param>
/// <param name="Sequence">The event's sequence.</param>
public sealed record SliceIndexEntry(string TenantId, object Identity, long Sequence);

/// <summary>
/// marten#6003: the slice index rows one projection batch writes, in the batch's transaction
/// (<see cref="IProjectionBatch.AppendSliceIndex" />). Rows are distinct; a store can insert them without a conflict
/// check, except that a rebuild flush may write a row a crashed earlier attempt left behind, so an upsert or "on
/// conflict do nothing" is the safe write.
/// </summary>
public sealed class SliceIndexRows
{
    public SliceIndexRows(string projectionName, uint projectionVersion, Type identityType,
        IReadOnlyList<SliceIndexEntry> entries)
    {
        ProjectionName = projectionName;
        ProjectionVersion = projectionVersion;
        IdentityType = identityType;
        Entries = entries;
    }

    /// <summary>The projection's name. One index per projection name and version.</summary>
    public string ProjectionName { get; }

    /// <summary>The projection's version: blue/green versions keep separate indexes.</summary>
    public uint ProjectionVersion { get; }

    /// <summary>The simple type of <see cref="SliceIndexEntry.Identity" />: Guid, string, int or long.</summary>
    public Type IdentityType { get; }

    public IReadOnlyList<SliceIndexEntry> Entries { get; }
}

/// <summary>
/// marten#6003: delete slice index rows that fell out of a <see cref="SliceIndexRetention.ForParking" /> window
/// (<see cref="IEventDatabase.PruneSliceIndexAsync" />).
/// </summary>
public sealed record SliceIndexPruneRequest
{
    public required string ProjectionName { get; init; }

    public uint ProjectionVersion { get; init; } = 1;

    /// <summary>
    /// The tenant of a tenant-partitioned shard, whose rows are the only ones to prune. Null prunes every tenant's
    /// rows of the projection version.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>Delete only rows at or below this sequence. Parked identities' rows are above it.</summary>
    public required long DeleteThrough { get; init; }

    /// <summary>Delete only rows whose event was appended before this time.</summary>
    public required DateTimeOffset OlderThan { get; init; }
}

/// <summary>
/// marten#6003: where a projection's slice index starts. The index holds every row above its mark, which is an
/// event-range mark (<see cref="IEventDatabase.FetchEventRangeMarkAsync" />) under <see cref="KeyFor" />.
/// </summary>
public static class SliceIndexMarks
{
    /// <summary>The event-range mark key of one projection version's slice index.</summary>
    public static string KeyFor(string projectionName, uint version) => $"slice-index:{projectionName}:{version}";

    /// <summary>The event-range mark key of the slice index a shard writes.</summary>
    public static string KeyFor(ShardName shard) => KeyFor(shard.Name, shard.Version);
}
