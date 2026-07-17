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
    ///     Read the current sequence + lifecycle state for a single (projection, tenant) cell directly
    ///     from the progression table, without spinning up an <see cref="Daemon.IProjectionDaemon" />.
    ///     Returns null when no row exists for the pair yet — e.g. the daemon has not yet observed this
    ///     projection for this tenant. A null <paramref name="tenantId" /> means store-global on a
    ///     non-tenanted store, or the default-tenant row on a tenanted store.
    ///     <para>
    ///     This is the targeted counterpart to <see cref="AllProjectionProgress" />, which returns every
    ///     row across every projection × tenant pair and leaves the caller to filter. For a per-cell UI
    ///     polling loop (e.g. 1Hz against a single visible batch) the targeted query is materially cheaper.
    ///     </para>
    ///     The default implementation throws <see cref="NotSupportedException" /> rather than returning
    ///     null: null is the meaningful "no row yet" answer, so a store that simply has not implemented
    ///     this must not borrow it and report a live cell as absent. Event stores (Marten, Polecat)
    ///     override this against their progression table. See jasperfx#435.
    /// </summary>
    /// <param name="projectionName">Name of the projection whose cell to read.</param>
    /// <param name="tenantId">Tenant owning the cell. Null means store-global / default tenant.</param>
    /// <param name="token"></param>
    ValueTask<ProjectionProgressRow?> ReadProjectionProgressAsync(
        string projectionName,
        string? tenantId,
        CancellationToken token)
        => throw new NotSupportedException(
            "ReadProjectionProgressAsync is not implemented on this IEventDatabase. Use an event store (Marten or Polecat) that supports reading a single projection progression row.");
}