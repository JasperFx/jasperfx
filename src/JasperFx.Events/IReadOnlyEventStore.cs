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

    /// <summary>
    /// The bodies of every event of type <typeparamref name="T"/>, across all streams, as a composable
    /// <see cref="IQueryable{T}"/> — the store-agnostic way to ask "every <c>OrderPlaced</c> with
    /// <c>Total &gt; 1000</c>" (jasperfx#1073). Marten's <c>QueryRawEventDataOnly&lt;T&gt;()</c>, on the
    /// shared read tier.
    /// </summary>
    /// <param name="tenantId">
    /// Tenant partition to scope the events to, by the same rule as <see cref="QueryStreamStates"/>: null
    /// means store-global, and a store without a tenant dimension MUST refuse a non-null tenant with a
    /// <see cref="NotSupportedException"/> naming it rather than silently return the global set.
    /// </param>
    /// <remarks>
    /// <para>
    /// The element is the event body, so a predicate is over one event type's own members. Only events
    /// stored as exactly <typeparamref name="T"/> are returned — another event type with the same shape is
    /// not. <c>Where()</c> must translate against the body's public members as the store's document LINQ
    /// does, plus <c>OrderBy</c>/<c>ThenBy</c>, <c>Skip</c> and <c>Take</c>; a member the provider cannot
    /// translate MUST fail the query naming it, never silently match every event — the jasperfx#737 failure
    /// mode. With no <c>OrderBy</c>, the order of the results is the store's to choose.
    /// </para>
    /// <para>
    /// Execute with the shared asynchronous terminators in <see cref="Documents.DocumentQueryableExtensions"/>
    /// (<c>ToListAsync</c>, <c>CountAsync</c>, <c>AnyAsync</c>, <c>FirstOrDefaultAsync</c>): the queryable's
    /// LINQ provider implements <see cref="Documents.IDocumentQueryExecutor"/>, as it does for
    /// <see cref="QueryStreamStates"/>.
    /// </para>
    /// <para>
    /// Not in this first cut: the envelope (sequence, stream, timestamp, tags) alongside the body, and a
    /// predicate across several event types at once. <see cref="QueryEventsAsync"/> filters on the envelope.
    /// </para>
    /// <para>
    /// Added in JasperFx 2.94 and default-implemented so that a store built against an older JasperFx still
    /// loads (jasperfx#933). The default throws <see cref="NotSupportedException"/> naming the implementing
    /// type.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation only, on a store built against a JasperFx older than 2.94.
    /// </exception>
    IQueryable<T> QueryEventData<T>(string? tenantId = null) where T : class
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IReadOnlyEventStore)}.{nameof(QueryEventData)}<T>, which was added in JasperFx 2.94. The store was built against an older JasperFx; upgrade the store package to a version built against JasperFx 2.94 or later.");
}
