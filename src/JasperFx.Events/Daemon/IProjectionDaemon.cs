#nullable enable
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     Starts, stops, and manages any running asynchronous projections
/// </summary>
public interface IProjectionDaemon: IDisposable
{
    /// <summary>
    /// Puts this daemon in a mode appropriate for rebuilding by stopping any
    /// running high water detection and running the high water detection once to
    /// set the ceiling for rebuilds
    /// </summary>
    /// <returns></returns>
    Task PrepareForRebuildsAsync();
    
    /// <summary>
    ///     Observable tracking of projection shard events
    /// </summary>
    ShardStateTracker Tracker { get; }

    /// <summary>
    /// Subject URI of the <see cref="IEventStore"/> this daemon serves (e.g.
    /// <c>marten://main</c>). Returned by the <see cref="IEventStore.Subject"/>
    /// the daemon was built against. Used by
    /// <see cref="ProjectionDaemonExtensions.SubscribeWithStoreUriStamp"/> to
    /// stamp <see cref="Projections.ShardState.StoreUri"/> on every state the
    /// daemon publishes, so a singleton observer attached to multiple stores'
    /// daemons can attribute callbacks to the right store.
    ///
    /// Null only if the daemon was constructed without a store (test
    /// scaffolding); production paths always have it. See
    /// JasperFx/ProductSupport#5.
    /// </summary>
    string? StoreUri => null;

    /// <summary>
    /// Indicates if this daemon is currently running any subscriptions
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     jasperfx#497 (the #420 leftover): the shared per-database budget on how many projection
    ///     rebuild cells — the (projection × tenant/shard) cross product — may replay concurrently
    ///     within this daemon's database. One budget spans BOTH fan-out layers, so a projection-level
    ///     rebuild slot and its per-tenant cells never multiply the bound. Null means "derive from
    ///     <see cref="DaemonSettings.MaxConcurrentRebuildsPerDatabase" /> /
    ///     <see cref="IEventStore.MaxConcurrentRebuildsPerDatabase" />"; a non-positive value is
    ///     unbounded. Setting it (e.g. the <c>projections rebuild --max-concurrent</c> CLI flag via
    ///     <see cref="CommandLine.ProjectionInput.ResolveMaxDegreeOfParallelism" />) replaces the
    ///     budget for subsequent rebuild operations. The default implementation ignores writes and
    ///     reports no budget, so daemons without cross-product rebuild fan-out are unaffected.
    /// </summary>
    int? MaxConcurrentRebuildsPerDatabase
    {
        get => null;
        set { }
    }

    /// <summary>
    ///     Rebuilds a single projection by projection name inline.
    ///     Will timeout if a shard takes longer than 5 minutes.
    /// </summary>
    /// <param name="projectionName"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task RebuildProjectionAsync(string projectionName, CancellationToken token);

    /// <summary>
    ///     Rebuilds a single projection by projection name for a single tenant partition inline.
    ///     A null <paramref name="tenantId" /> is store-global and delegates to the tenant-less overload
    ///     (today's behavior). Daemons that implement per-tenant partitioning override this; the default
    ///     throws for a non-null tenant. See jasperfx#407.
    /// </summary>
    Task RebuildProjectionAsync(string projectionName, string? tenantId, CancellationToken token)
        => tenantId == null
            ? RebuildProjectionAsync(projectionName, token)
            : throw new NotSupportedException(
                "Per-tenant RebuildProjectionAsync is not implemented on this IProjectionDaemon. Use an event store that implements per-tenant partitioning.");


    /// <summary>
    ///     Rebuilds a single projection by projection type inline.
    ///     Will timeout if a shard takes longer than 5 minutes.
    /// </summary>
    /// <typeparam name="TView">Projection view type</typeparam>
    /// <param name="token"></param>
    /// <returns></returns>
    Task RebuildProjectionAsync<TView>(CancellationToken token);

    /// <summary>
    ///     Rebuilds a single projection by projection type inline.
    ///     Will timeout if a shard takes longer than 5 minutes.
    /// </summary>
    /// <param name="projectionType">The projection type</param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task RebuildProjectionAsync(Type projectionType, CancellationToken token);

    /// <summary>
    ///     Rebuilds a single projection by projection name inline
    /// </summary>
    /// <param name="projectionType">The projection type</param>
    /// <param name="shardTimeout"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task RebuildProjectionAsync(Type projectionType, TimeSpan shardTimeout, CancellationToken token);

    /// <summary>
    ///     Rebuilds a single projection by projection name inline
    /// </summary>
    /// <param name="projectionName"></param>
    /// <param name="shardTimeout"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task RebuildProjectionAsync(string projectionName, TimeSpan shardTimeout, CancellationToken token);

    /// <summary>
    ///     Rebuilds a single projection by projection name for a single tenant partition inline.
    ///     A null <paramref name="tenantId" /> is store-global and delegates to the tenant-less overload
    ///     (today's behavior). Daemons that implement per-tenant partitioning override this; the default
    ///     throws for a non-null tenant. See jasperfx#407.
    /// </summary>
    Task RebuildProjectionAsync(string projectionName, string? tenantId, TimeSpan shardTimeout, CancellationToken token)
        => tenantId == null
            ? RebuildProjectionAsync(projectionName, shardTimeout, token)
            : throw new NotSupportedException(
                "Per-tenant RebuildProjectionAsync is not implemented on this IProjectionDaemon. Use an event store that implements per-tenant partitioning.");


    /// <summary>
    ///     Rebuilds a single projection by projection type inline
    /// </summary>
    /// <typeparam name="TView">Projection view type</typeparam>
    /// <param name="token"></param>
    /// <returns></returns>
    Task RebuildProjectionAsync<TView>(TimeSpan shardTimeout, CancellationToken token);

    /// <summary>
    ///     Starts a single projection shard by name
    /// </summary>
    /// <param name="shardName">The full identity of the desired shard. Example 'Trip:All'</param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task StartAgentAsync(string shardName, CancellationToken token);
    
    /// <summary>
    ///     Starts a single projection shard by name
    /// </summary>
    /// <param name="shardName">The full identity of the desired shard. Example 'Trip:All'</param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<ISubscriptionAgent> StartAgentAsync(ShardName name, CancellationToken token);

    /// <summary>
    ///     Stops a single projection shard by name
    /// </summary>
    /// <param name="shardName">The full identity of the desired shard. Example 'Trip:All'</param>
    /// <param name="ex"></param>
    /// <returns></returns>
    Task StopAgentAsync(string shardName, Exception? ex = null);
    
    /// <summary>
    ///     Stops a single projection shard by name
    /// </summary>
    /// <param name="shardName">The full identity of the desired shard. Example 'Trip:All'</param>
    /// <param name="ex"></param>
    /// <returns></returns>
    Task StopAgentAsync(ShardName shardName, Exception? ex = null);

    /// <summary>
    ///     Pauses (hard-stops) the running shard(s) of a single projection for a single tenant
    ///     partition, leaving every other tenant's shard running. A null <paramref name="tenantId" />
    ///     pauses the projection store-globally (all of its shards). Daemons that implement per-tenant
    ///     event partitioning override this; the default throws for a non-null tenant. Resume with
    ///     <see cref="StartAgentAsync(ShardName, CancellationToken)" /> or <see cref="StartAllAsync" />.
    ///     Unlike <see cref="StopAgentAsync(ShardName, Exception)" />, the caller does not need to know
    ///     the exact shard identity (shard key / version) — the shards are matched by projection name and
    ///     tenant. See CritterWatch#303.
    /// </summary>
    Task PauseShardAsync(string projectionName, string? tenantId, CancellationToken token)
        => tenantId == null
            ? throw new NotImplementedException(
                "Store-global PauseShardAsync is not implemented on this IProjectionDaemon.")
            : throw new NotSupportedException(
                "Per-tenant PauseShardAsync is not implemented on this IProjectionDaemon. Use an event store that implements per-tenant partitioning.");

    /// <summary>
    ///     Starts all known projections shards
    /// </summary>
    /// <returns></returns>
    Task StartAllAsync();

    /// <summary>
    /// Optimized mechanism to advance all asynchronous projections
    /// and subscriptions to the high water mark inline. This is meant
    /// for test automation and assumes a relatively small database size
    ///
    /// This will first stop any running agents before continuing. You will have to
    /// explicitly restart the daemon after executing this
    ///
    /// Not meant for production usage!
    /// </summary>
    /// <returns></returns>
    Task CatchUpAsync(CancellationToken cancellation);

    /// <summary>
    /// Optimized mechanism to advance all asynchronous projections
    /// and subscriptions to the high water mark inline. This is meant
    /// for test automation and assumes a relatively small database size
    ///
    /// This will first stop any running agents before continuing. You will have to
    /// explicitly restart the daemon after executing this
    ///
    /// Not meant for production usage!
    /// </summary>
    /// <param name="timeout">Maximum time to wait for catch up to complete</param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task CatchUpAsync(TimeSpan timeout, CancellationToken cancellation);

    /// <summary>
    ///     Stops all known projection shards
    /// </summary>
    /// <returns></returns>
    Task StopAllAsync();

    /// <summary>
    ///     Use with caution! This will try to wait for all projections to "catch up" to the currently
    ///     known farthest known sequence of the event store
    /// </summary>
    /// <param name="timeout"></param>
    /// <returns></returns>
    Task WaitForNonStaleData(TimeSpan timeout);

    public long HighWaterMark();
    AgentStatus StatusFor(string shardName);

    /// <summary>
    /// List of agents that are currently running or paused
    /// </summary>
    /// <returns></returns>
    IReadOnlyList<ISubscriptionAgent> CurrentAgents();

    /// <summary>
    /// Are there any paused agents?
    /// </summary>
    /// <returns></returns>
    bool HasAnyPaused();

    /// <summary>
    /// Will eject a Paused
    /// </summary>
    /// <param name="shardName"></param>
    void EjectPausedShard(string shardName);

    /// <summary>
    /// Wait until the named shard has been started
    /// </summary>
    /// <param name="shardName"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    Task WaitForShardToBeRunning(string shardName, TimeSpan timeout);


    /// <summary>
    /// Rewinds a subscription (or projection, so be careful with this usage) to a certain point
    /// and allows it to restart at that point
    /// </summary>
    /// <param name="subscriptionName">Name of the subscription</param>
    /// <param name="token"></param>
    /// <param name="sequenceFloor">The point at which to rewind the subscription. The default is zero</param>
    /// <param name="timestamp">Optional parameter to rewind the subscription to rerun any events that were posted on or after this time. If the system cannot determine the sequence, it will do nothing</param>
    /// <returns></returns>
    Task RewindSubscriptionAsync(string subscriptionName, CancellationToken token, long? sequenceFloor = 0,
        DateTimeOffset? timestamp = null);

    /// <summary>
    /// Rewinds a subscription (or projection) for a single tenant partition to a certain point and
    /// allows it to restart at that point. A null <paramref name="tenantId" /> is store-global and
    /// delegates to the tenant-less overload (today's behavior). Daemons that implement per-tenant
    /// partitioning override this; the default throws for a non-null tenant. See jasperfx#407.
    /// </summary>
    /// <param name="subscriptionName">Name of the subscription</param>
    /// <param name="tenantId">Tenant partition to scope the rewind to. Null means store-global.</param>
    /// <param name="token"></param>
    /// <param name="sequenceFloor">The point at which to rewind the subscription. The default is zero</param>
    /// <param name="timestamp">Optional parameter to rewind the subscription to rerun any events that were posted on or after this time. If the system cannot determine the sequence, it will do nothing</param>
    Task RewindSubscriptionAsync(string subscriptionName, string? tenantId, CancellationToken token,
        long? sequenceFloor = 0, DateTimeOffset? timestamp = null)
        => tenantId == null
            ? RewindSubscriptionAsync(subscriptionName, token, sequenceFloor, timestamp)
            : throw new NotSupportedException(
                "Per-tenant RewindSubscriptionAsync is not implemented on this IProjectionDaemon. Use an event store that implements per-tenant partitioning.");
}
