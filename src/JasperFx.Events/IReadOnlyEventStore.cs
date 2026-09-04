namespace JasperFx.Events;

/// <summary>
/// Read-only event store operations for querying streams and events.
/// Implemented by Marten and any future event store providers.
/// </summary>
public interface IReadOnlyEventStore
{
    /// <summary>
    /// Fetch all events for a stream identified by Guid
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchStreamAsync(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default);

    /// <summary>
    /// Fetch all events for a stream identified by string key
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchStreamAsync(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default);

    /// <summary>
    /// Fetch stream metadata by Guid
    /// </summary>
    Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken token = default);

    /// <summary>
    /// Fetch stream metadata by string key
    /// </summary>
    Task<StreamState?> FetchStreamStateAsync(string streamKey, CancellationToken token = default);

    /// <summary>
    /// Query events across all streams with filtering and pagination. Results are ordered by the
    /// store-global event sequence, ascending — oldest first — and the query's paging walks that
    /// ordering, with <see cref="PagedEvents.TotalCount"/> reporting the total number of matching
    /// events across every page.
    /// </summary>
    /// <remarks>
    /// Implementations MUST either honor every filter supplied on <paramref name="query"/> or refuse
    /// the query with a <see cref="NotSupportedException"/> naming the unsupported field — never
    /// silently ignore a filter, because unfiltered results read as filtered. Call
    /// <see cref="EventQuery.AssertFiltersAreSupported"/> first thing, declaring the
    /// <see cref="EventQueryFilters"/> the implementation honors, to get that behavior consistently.
    /// See jasperfx#737.
    /// </remarks>
    Task<PagedEvents> QueryEventsAsync(EventQuery query, CancellationToken token = default);

    /// <summary>
    /// The streams table as a composable <see cref="IQueryable{T}"/> of <see cref="StreamState"/> —
    /// the store-agnostic way to ask questions like "every stream of aggregate type X whose
    /// un-compacted growth exceeds N" (the Stream Compaction Policy selector, jasperfx#740).
    /// </summary>
    /// <param name="tenantId">
    /// Tenant partition to scope the streams to. Null means store-global (the default session's
    /// scope). A store without a tenant dimension MUST refuse a non-null tenant with a
    /// <see cref="NotSupportedException"/> naming it, never silently return the global set — the
    /// same rule <see cref="QueryEventsAsync"/> applies to <see cref="EventQuery.TenantId"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <c>Where()</c> must translate against every public get member of <see cref="StreamState"/> —
    /// <see cref="StreamState.Id"/>, <see cref="StreamState.Key"/>, <see cref="StreamState.Version"/>,
    /// <see cref="StreamState.AggregateType"/> (equality against a <c>typeof(X)</c> constant,
    /// translated to the stored aggregate-type identity), <see cref="StreamState.LastTimestamp"/>,
    /// <see cref="StreamState.Created"/>, <see cref="StreamState.IsArchived"/> and
    /// <see cref="StreamState.CompactedVersion"/> — plus <c>OrderBy</c>/<c>ThenBy</c>, <c>Skip</c>
    /// and <c>Take</c> over the same members. A member a provider cannot translate MUST fail the
    /// query with an exception naming that member (normally at translation time), never silently
    /// match all rows or drop the clause: an ignored predicate returns unfiltered streams that read
    /// as filtered, the jasperfx#737 failure mode this surface exists to refuse.
    /// </para>
    /// <para>
    /// Execute with the shared asynchronous terminators in
    /// <see cref="Documents.DocumentQueryableExtensions"/> (<c>ToListAsync</c>, <c>CountAsync</c>,
    /// <c>AnyAsync</c>, <c>FirstOrDefaultAsync</c>): the returned queryable's LINQ provider
    /// implements <see cref="Documents.IDocumentQueryExecutor"/>, the same execution hook the
    /// document read tier uses, so stores implement exactly one async dispatch for both surfaces.
    /// </para>
    /// <para>
    /// Deliberately abstract rather than a throwing default implementation: the compile break on
    /// the next package bump is what forces every store to implement in the same wave, mirroring
    /// <see cref="QueryEventsAsync"/>. And unlike the old rationale for keeping
    /// <c>QueryAllRawEvents</c> off the shared read tier — store-specific queryable types — this
    /// contract is expressible abstractly, because <see cref="StreamState"/> is a
    /// JasperFx.Events-owned type.
    /// </para>
    /// </remarks>
    IQueryable<StreamState> QueryStreamStates(string? tenantId = null);
}
