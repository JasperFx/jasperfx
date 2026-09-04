namespace JasperFx.Events.CommandLine;

/// <summary>
/// The whole result of one <c>stream-query</c> run, in the shape agents and scripts consume from
/// the default JSON output. A failed run is a report too — <see cref="Error"/> is populated and
/// <see cref="Streams"/> is empty — and an empty page with <see cref="TotalCount"/> 0 and a null
/// <see cref="Error"/> is a real answer, not a failure.
/// </summary>
/// <param name="Query">Echo of the effective filters, so a stored report is self-describing and reproducible.</param>
/// <param name="Store">Subject uri of the event store that answered.</param>
/// <param name="Streams">The requested page, in the command's stated ordering: creation order (oldest first), ties broken by stream identity.</param>
/// <param name="TotalCount">Total matching streams across every page.</param>
/// <param name="PageNumber">Echo of the requested page number.</param>
/// <param name="PageSize">Echo of the requested page size.</param>
/// <param name="HasMore">True when pages after this one still hold matching streams.</param>
/// <param name="Error">Operator-facing failure message, or null when the query ran.</param>
/// <param name="Diagnosis">
/// What to do about <paramref name="Error"/> when the cause is known — today, storage that was
/// never migrated. Same disposition as <see cref="ProjectionRunReport.Diagnosis"/>.
/// </param>
public sealed record StreamQueryReport(
    StreamQueryFilterReport Query,
    string? Store,
    IReadOnlyList<StreamQueryStreamReport> Streams,
    int TotalCount,
    int PageNumber,
    int PageSize,
    bool HasMore,
    string? Error,
    string? Diagnosis = null)
{
    public static StreamQueryReport From(
        StreamQueryInput input, Type? aggregateType, Uri? store,
        IReadOnlyList<StreamState> page, int totalCount)
    {
        var streams = page.Select(StreamQueryStreamReport.From).ToArray();

        return new StreamQueryReport(
            StreamQueryFilterReport.From(input, aggregateType), store?.ToString(), streams,
            totalCount, input.PageFlag, input.PageSizeFlag,
            HasMore: (long)input.PageFlag * input.PageSizeFlag < totalCount, Error: null);
    }

    /// <summary>A run that never produced a page: the filters are still worth echoing.</summary>
    public static StreamQueryReport Failed(StreamQueryInput input, Type? aggregateType, Uri? store,
        string error, string? diagnosis = null)
        => new(StreamQueryFilterReport.From(input, aggregateType), store?.ToString(), [], 0,
            input.PageFlag, input.PageSizeFlag, false, error, diagnosis);
}

/// <summary>
/// The effective filters a <see cref="StreamQueryReport"/> was produced under, carried in full so
/// a stored report is self-describing — the same fields the run would need to be reproduced.
/// </summary>
/// <param name="AggregateType">Full CLR name of the resolved aggregate type filter, or null.</param>
/// <param name="MinVersion">Inclusive lower bound on the stream version, or null.</param>
/// <param name="VersionAboveCompacted">The compaction-policy growth threshold (Version - CompactedVersion must exceed it), or null.</param>
/// <param name="Archived">The archived-flag filter, or null when both archived and live streams were included.</param>
/// <param name="CreatedFrom">Inclusive lower bound on the creation time, or null.</param>
/// <param name="CreatedTo">Inclusive upper bound on the creation time, or null.</param>
/// <param name="UpdatedFrom">Inclusive lower bound on the last-append time, or null.</param>
/// <param name="UpdatedTo">Inclusive upper bound on the last-append time, or null.</param>
/// <param name="TenantId">Tenant the query was scoped to, or null for store-global.</param>
public sealed record StreamQueryFilterReport(
    string? AggregateType,
    long? MinVersion,
    long? VersionAboveCompacted,
    bool? Archived,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    DateTimeOffset? UpdatedFrom,
    DateTimeOffset? UpdatedTo,
    string? TenantId)
{
    public static StreamQueryFilterReport From(StreamQueryInput input, Type? aggregateType)
        => new(aggregateType?.FullName, input.MinVersionFlag, input.VersionAboveCompactedFlag,
            input.ArchivedFlag, input.CreatedFrom, input.CreatedTo, input.UpdatedFrom, input.UpdatedTo,
            input.TenantFlag);
}

/// <summary>
/// One stream of a <see cref="StreamQueryReport"/> page.
/// </summary>
/// <param name="StreamId">String form of the stream identity (Guid or string key).</param>
/// <param name="Version">Current version of the stream — its event count high-water.</param>
/// <param name="CompactedVersion">The compaction watermark; 0 when the stream has never been compacted.</param>
/// <param name="VersionsSinceCompaction">
/// <paramref name="Version"/> minus <paramref name="CompactedVersion"/> — the un-compacted growth
/// the compaction policy thresholds on, precomputed so a consumer never re-derives it.
/// </param>
/// <param name="AggregateType">Full CLR name of the aggregate type the stream was started as, or null.</param>
/// <param name="Created">When the stream was created.</param>
/// <param name="LastTimestamp">When the stream was last appended to.</param>
/// <param name="IsArchived">Whether the stream is archived.</param>
public sealed record StreamQueryStreamReport(
    string StreamId,
    long Version,
    long CompactedVersion,
    long VersionsSinceCompaction,
    string? AggregateType,
    DateTimeOffset Created,
    DateTimeOffset LastTimestamp,
    bool IsArchived)
{
    public static StreamQueryStreamReport From(StreamState state)
        => new(state.Key ?? state.Id.ToString(), state.Version, state.CompactedVersion,
            state.Version - state.CompactedVersion, state.AggregateType?.FullName,
            state.Created, state.LastTimestamp, state.IsArchived);
}
