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
}
