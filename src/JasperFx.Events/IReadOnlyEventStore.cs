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
    /// Query events across all streams with filtering and pagination
    /// </summary>
    Task<PagedEvents> QueryEventsAsync(EventQuery query, CancellationToken token = default);
}
