using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// The whole result of one <c>event-query</c> run, in the shape agents and scripts consume from
/// the default JSON output. A failed run is a report too — <see cref="Error"/> is populated and
/// <see cref="Events"/> is empty — so a consumer never has to distinguish "no output" from "no
/// matches". An empty page with <see cref="TotalCount"/> 0 and a null <see cref="Error"/> is a
/// real answer, not a failure.
/// </summary>
/// <param name="Query">Echo of the query that ran, so a stored report is self-describing and reproducible.</param>
/// <param name="Store">Subject uri of the event store that answered.</param>
/// <param name="Events">The requested page, ordered by store-global sequence ascending.</param>
/// <param name="TotalCount">Total matching events across every page.</param>
/// <param name="PageNumber">Echo of the requested page number.</param>
/// <param name="PageSize">Echo of the requested page size.</param>
/// <param name="HasMore">True when pages after this one still hold matching events.</param>
/// <param name="Error">Operator-facing failure message, or null when the query ran.</param>
/// <param name="Diagnosis">
/// What to do about <paramref name="Error"/> when the cause is known — today, storage that was
/// never migrated. Same disposition as <see cref="ProjectionRunReport.Diagnosis"/>.
/// </param>
public sealed record EventQueryReport(
    EventQuery Query,
    string? Store,
    IReadOnlyList<EventQueryEventReport> Events,
    int TotalCount,
    int PageNumber,
    int PageSize,
    bool HasMore,
    string? Error,
    string? Diagnosis = null)
{
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Reflective JSON serialization of arbitrary event payloads. The event-query command is a development-time CLI surface, not part of an AOT-published runtime — same disposition as ProjectionRunView.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Same as IL2026.")]
    public static EventQueryReport From(EventQuery query, Uri? store, PagedEvents page, bool includePayloads)
    {
        var events = page.Events.Select(e => new EventQueryEventReport(
            e.Sequence,
            e.StreamKey ?? e.StreamId.ToString(),
            e.Version,
            e.EventTypeName,
            e.Timestamp,
            e.TenantId,
            e.CorrelationId,
            e.CausationId,
            e.UserName,
            includePayloads && e.Data != null
                ? JsonSerializer.SerializeToElement(e.Data, e.Data.GetType())
                : null)).ToArray();

        return new EventQueryReport(query, store?.ToString(), events, page.TotalCount, page.PageNumber,
            page.PageSize, HasMore: (long)page.PageNumber * page.PageSize < page.TotalCount, Error: null);
    }

    /// <summary>A run that never produced a page: the query is still worth echoing.</summary>
    public static EventQueryReport Failed(EventQuery query, Uri? store, string error, string? diagnosis = null)
        => new(query, store?.ToString(), [], 0, query.PageNumber, query.PageSize, false, error, diagnosis);
}

/// <summary>
/// One event of an <see cref="EventQueryReport"/> page: the shared envelope, with the payload
/// included unless the run asked for metadata only.
/// </summary>
/// <param name="Sequence">Store-global sequence number.</param>
/// <param name="StreamId">String form of the stream identity (Guid or string key).</param>
/// <param name="Version">Per-stream version of the event.</param>
/// <param name="EventType">Event-type alias from the store's registry.</param>
/// <param name="Timestamp">Server-assigned append time.</param>
/// <param name="TenantId">Tenant the event belongs to.</param>
/// <param name="CorrelationId">Correlation id metadata, when captured.</param>
/// <param name="CausationId">Causation id metadata, when captured.</param>
/// <param name="UserName">User name metadata, when captured.</param>
/// <param name="Data">The event payload, or null when <c>--no-payloads</c> omitted it.</param>
public sealed record EventQueryEventReport(
    long Sequence,
    string StreamId,
    long Version,
    string EventType,
    DateTimeOffset Timestamp,
    string? TenantId,
    string? CorrelationId,
    string? CausationId,
    string? UserName,
    JsonElement? Data);
