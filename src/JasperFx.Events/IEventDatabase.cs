using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events;

public interface IEventDatabase 
{
    /// <summary>
    ///     Identifying name for infrastructure and logging
    /// </summary>
    string Identifier { get; }
    
    Uri DatabaseUri { get; }
    
    /// <summary>
    ///     *If* a projection daemon has been started for this database, this
    ///     is the ShardStateTracker for the running daemon. This is useful in testing
    ///     scenarios
    /// </summary>
    ShardStateTracker Tracker { get; }

    /// <summary>
    /// Store a dead letter event for a failed event in a projection or subscription
    /// </summary>
    /// <param name="storage">The parent event store</param>
    /// <param name="deadLetterEvent"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token);

    Task EnsureStorageExistsAsync(Type storageType, CancellationToken token);

    Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout);
    
    /// <summary>
    ///     Check the current progress of a single projection or projection shard
    /// </summary>
    /// <param name="tenantId">
    ///     Specify the database containing this tenant id. If omitted, this method uses the default
    ///     database
    /// </param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<long> ProjectionProgressFor(ShardName name,
        CancellationToken token = default);

    /// <summary>
    /// Find the position of the event store sequence just below the supplied timestamp. Will
    /// return null if there are no events below that time threshold
    /// </summary>
    /// <param name="timestamp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token);
    
    string StorageIdentifier { get; }
    Task<long> FetchHighestEventSequenceNumber(CancellationToken token);
    
    /// <summary>
    ///     Check the current progress of all asynchronous projections
    /// </summary>
    /// <param name="token"></param>
    /// <param name="tenantId">
    ///     Specify the database containing this tenant id. If omitted, this method uses the default
    ///     database
    /// </param>
    /// <returns></returns>
    Task<IReadOnlyList<ShardState>> AllProjectionProgress(
        CancellationToken token = default);
}