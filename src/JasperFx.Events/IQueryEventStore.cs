namespace JasperFx.Events;

/// <summary>
/// Read-only event-store API as seen from a session. Canonical home for the surface
/// that Marten's <c>IQueryEventStore</c> and Polecat's <c>IQueryEventStore</c> share.
/// </summary>
/// <remarks>
/// LINQ-returning methods (<c>QueryRawEventDataOnly</c>, <c>QueryAllRawEvents</c>) are
/// intentionally not part of this contract — they return product-specific queryable
/// types and stay on each product's product-specific extension of this interface.
/// </remarks>
public interface IQueryEventStore
{
    /// <summary>
    /// Fetch all of the events for the named stream.
    /// </summary>
    /// <param name="streamId">Stream identity (Guid).</param>
    /// <param name="version">If set, queries for events up to and including this version.</param>
    /// <param name="timestamp">If set, queries for events captured on or before this timestamp.</param>
    /// <param name="fromVersion">If set, queries for events on or from this version.</param>
    /// <param name="token">Cancellation token.</param>
    Task<IReadOnlyList<IEvent>> FetchStreamAsync(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default);

    /// <summary>
    /// Fetch all of the events for the named stream by string key.
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchStreamAsync(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default);

    /// <summary>
    /// Perform a live aggregation of the raw events in this stream into a T object.
    /// </summary>
    Task<T?> AggregateStreamAsync<T>(Guid streamId, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class;

    /// <summary>
    /// Perform a live aggregation of the raw events in this stream into a T object.
    /// </summary>
    Task<T?> AggregateStreamAsync<T>(string streamKey, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class;

    /// <summary>
    /// Perform a live aggregation but return the last known non-null version of the aggregate,
    /// walking backwards through events if the aggregate is marked deleted at the current version.
    /// </summary>
    Task<T?> AggregateStreamToLastKnownAsync<T>(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, CancellationToken token = default) where T : class;

    /// <summary>
    /// Perform a live aggregation but return the last known non-null version of the aggregate,
    /// walking backwards through events if the aggregate is marked deleted at the current version.
    /// </summary>
    Task<T?> AggregateStreamToLastKnownAsync<T>(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, CancellationToken token = default) where T : class;

    /// <summary>
    /// Load a single event by its id, knowing the event type upfront.
    /// </summary>
    Task<IEvent<T>?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class;

    /// <summary>
    /// Load a single event by its id.
    /// </summary>
    Task<IEvent?> LoadAsync(Guid id, CancellationToken token = default);

    /// <summary>
    /// Fetch only the metadata about a stream by id.
    /// </summary>
    Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken token = default);

    /// <summary>
    /// Fetch only the metadata about a stream by string key.
    /// </summary>
    Task<StreamState?> FetchStreamStateAsync(string streamKey, CancellationToken token = default);
}
