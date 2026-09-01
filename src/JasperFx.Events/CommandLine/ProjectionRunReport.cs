using System.Text.Json;
using JasperFx.Descriptors;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// The whole result of one <c>projection-run</c>, in the shape agents and scripts consume from
/// <c>--json</c>. A failed run is a report too — <see cref="Error"/> is populated and
/// <see cref="Aggregates"/> is empty — so a consumer never has to distinguish "no output" from
/// "no aggregates".
/// </summary>
/// <param name="Projection">Registered name of the projection that was replayed.</param>
/// <param name="Store">Subject uri of the event store the source events came from.</param>
/// <param name="Source">The source slice that was read.</param>
/// <param name="EventCount">Number of source events fed into the projection.</param>
/// <param name="Truncated">True when <c>--max-events</c> stopped the source read early.</param>
/// <param name="Aggregates">One timeline per aggregate identity the projection touched, ordered by identity.</param>
/// <param name="Error">Operator-facing failure message, or null when the run succeeded.</param>
/// <param name="Diagnosis">
/// What to do about <paramref name="Error"/> when the cause is known — today, storage that was never
/// migrated. Null when the failure was not diagnosed. Carried as its own field rather than folded into
/// <paramref name="Error"/> so an agent can act on the remedy without parsing prose out of the message.
/// </param>
public sealed record ProjectionRunReport(
    string Projection,
    string? Store,
    ProjectionRunSourceReport Source,
    int EventCount,
    bool Truncated,
    IReadOnlyList<ProjectionRunAggregateReport> Aggregates,
    string? Error,
    string? Diagnosis = null)
{
    /// <summary>
    /// Fan the per-identity timelines out into report rows. Dictionary order is not guaranteed by
    /// <see cref="MultiAggregateProjectionResult"/>, so identities are sorted for a stable diff
    /// between two runs of the same slice.
    /// </summary>
    public static ProjectionRunReport From(
        ProjectionRunInput input, Uri? store, MultiAggregateProjectionResult result, int eventCount, bool truncated)
    {
        var aggregates = result.AggregatesByIdentity
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new ProjectionRunAggregateReport(
                kvp.Key,
                kvp.Value.Steps.Select(toStep).ToArray(),
                kvp.Value.FinalState))
            .ToArray();

        return new ProjectionRunReport(input.ProjectionName, store?.ToString(), ProjectionRunSourceReport.From(input),
            eventCount, truncated, aggregates, null);
    }

    /// <summary>A run that never reached the projection: the source slice is still worth reporting.</summary>
    public static ProjectionRunReport Failed(ProjectionRunInput input, Uri? store, string error,
        string? diagnosis = null)
        => new(input.ProjectionName, store?.ToString(), ProjectionRunSourceReport.From(input), 0, false, [], error,
            diagnosis);

    private static ProjectionRunStepReport toStep(ProjectionStepResultRaw step, int index)
        => new(index + 1, step.Event.Sequence, step.Event.StreamVersion, step.Event.StreamId,
            step.Event.EventTypeName, step.Event.Timestamp, step.Elapsed.TotalMilliseconds,
            step.Before, step.After, step.Error);
}

/// <summary>
/// The source slice a <see cref="ProjectionRunReport"/> was produced from, carried in full so a
/// stored report is self-describing — the same fields the run would need to be reproduced.
/// </summary>
/// <param name="Mode">Source-mode discriminator.</param>
/// <param name="Key">Stable encoding of the slice, matching CritterWatch's source key.</param>
/// <param name="StreamId">Stream the events were read from; null in tag-query mode.</param>
/// <param name="FromVersion">Inclusive lower version bound; null outside slice mode.</param>
/// <param name="ToVersion">Inclusive upper version bound; null outside slice mode.</param>
/// <param name="Tags">Tag map that was matched; null outside tag-query mode.</param>
/// <param name="TenantId">Tenant the source read was scoped to; null for a store-global read.</param>
public sealed record ProjectionRunSourceReport(
    ProjectionRunSourceMode Mode,
    string Key,
    string? StreamId,
    long? FromVersion,
    long? ToVersion,
    IReadOnlyDictionary<string, string>? Tags,
    string? TenantId)
{
    public static ProjectionRunSourceReport From(ProjectionRunInput input)
        => new(input.SourceMode, input.SourceKey,
            input.SourceMode == ProjectionRunSourceMode.TagQuery ? null : input.StreamFlag,
            input.FromFlag, input.ToFlag,
            input.SourceMode == ProjectionRunSourceMode.TagQuery ? input.TagFlag : null,
            input.TenantFlag);
}

/// <summary>
/// One aggregate identity's timeline. A single-stream projection produces exactly one of these;
/// a multi-stream projection produces one per identity the source slice touched.
/// </summary>
/// <param name="Identity">String form of the aggregate identity.</param>
/// <param name="Steps">Per-event steps in apply order.</param>
/// <param name="FinalState">State after the last applied event, or null when no event produced state.</param>
public sealed record ProjectionRunAggregateReport(
    string Identity,
    IReadOnlyList<ProjectionRunStepReport> Steps,
    JsonElement? FinalState);

/// <summary>
/// One step of a timeline: the event that was applied and the state either side of the apply.
/// </summary>
/// <param name="Step">1-based position of this step within the timeline.</param>
/// <param name="Sequence">Store-wide sequence number of the event.</param>
/// <param name="StreamVersion">Per-stream version of the event.</param>
/// <param name="StreamId">Stream the event belongs to.</param>
/// <param name="EventType">Event-type alias from the store's registry.</param>
/// <param name="Timestamp">Server-assigned append time of the event.</param>
/// <param name="ElapsedMs">Wall-clock milliseconds spent inside the projection's apply for this step.</param>
/// <param name="Before">State before the apply.</param>
/// <param name="After">State after the apply; equal to <paramref name="Before"/> when the apply threw.</param>
/// <param name="Error">Message of the exception the apply threw, or null when it succeeded.</param>
public sealed record ProjectionRunStepReport(
    int Step,
    long Sequence,
    long StreamVersion,
    string StreamId,
    string EventType,
    DateTimeOffset Timestamp,
    double ElapsedMs,
    JsonElement? Before,
    JsonElement? After,
    string? Error);
