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

    /// <summary>
    ///     Count the stored dead letter events for a single projection/subscription shard. With
    ///     <c>SkipApplyErrors</c> on (the JasperFx.Events 2.0 default), a failed <c>Apply()</c> is
    ///     recorded as a <see cref="DeadLetterEvent" /> and the shard keeps advancing, so the
    ///     accumulation of these is the primary "this projection is unhealthy" signal.
    ///     The default implementation returns 0 as a stand-in; event stores that persist dead
    ///     letters should override this. See jasperfx#356.
    /// </summary>
    /// <param name="shard">The projection/subscription shard to count dead letters for.</param>
    /// <param name="token"></param>
    Task<long> CountDeadLetterEventsAsync(ShardName shard, CancellationToken token = default)
        => Task.FromResult(0L);

    /// <summary>
    ///     Fetch the stored dead letter event counts for this database, one row per shard
    ///     (<see cref="DeadLetterShardCount.ProjectionName" /> + <see cref="DeadLetterShardCount.ShardKey" />).
    ///     Mirrors the "give me every row" shape of <see cref="AllProjectionProgress" />.
    ///     The default implementation returns an empty list as a stand-in; event stores that
    ///     persist dead letters should override this. See jasperfx#356.
    /// </summary>
    /// <param name="token"></param>
    Task<IReadOnlyList<DeadLetterShardCount>> FetchDeadLetterCountsAsync(CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<DeadLetterShardCount>>([]);
}