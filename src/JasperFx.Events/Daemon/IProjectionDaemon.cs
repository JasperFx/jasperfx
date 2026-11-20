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
    ///     Starts a single projection shard in <see cref="ShardExecutionMode.Rebuild" /> mode, following the event
    ///     store as it grows instead of stopping at the high-water mark, and resuming from the shard's own persisted
    ///     progress instead of rewinding it. Rebuild mode publishes no side effects and applies the rebuild error
    ///     handling. Nothing is torn down: the shard picks up exactly where its last run left off, or starts from the
    ///     beginning if it has never run.
    ///
    ///     Meant for warming up a new projection version on a node that is not serving yet -- a blue/green warm-up
    ///     (wolverine#4569). Starting the shard again later with <see cref="StartAgentAsync(ShardName, CancellationToken)" />
    ///     continues in continuous mode from the same progress.
    /// </summary>
    /// <param name="name">The shard to start, including its tenant if it has one</param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<ISubscriptionAgent> StartAgentInRebuildModeAsync(ShardName name, CancellationToken token)
    {
        throw new NotSupportedException($"{GetType().FullName} does not support starting a shard in rebuild mode");
    }

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

    /// <summary>
    /// jasperfx#539: heartbeat of the last completed high-water poll cycle — proof the high-water path is
    /// *cycling*, independent of whether the mark is *advancing*. Delegates to whichever path is active
    /// (store-global agent or the per-tenant coordinator). Null until the first cycle completes. Lets a
    /// health check distinguish "no new events" from "the high-water agent died" without the mark alone.
    /// </summary>
    DateTimeOffset? HighWaterLastPolledAt { get; }

    /// <summary>
    /// jasperfx#539: true when the active high-water path has not completed a poll cycle within the
    /// configured <see cref="IReadOnlyDaemonSettings.HighWaterStalenessThreshold"/> — i.e. the loop is
    /// alive-but-wedged. Measured against heartbeat age, NOT against the mark advancing, so a quiet store
    /// with no new events is never reported stale.
    /// </summary>
    bool IsHighWaterStale { get; }

    /// <summary>
    /// jasperfx#539: local restart seam for the high-water path. Restarts whichever path is active without
    /// ever advancing the mark. Intended for a health check that has observed staleness and wants to
    /// remediate in-process.
    /// </summary>
    /// <param name="token"></param>
    Task RestartHighWaterAgentAsync(CancellationToken token);

    AgentStatus StatusFor(string shardName);

    /// <summary>
    /// jasperfx#1009: is this shard's projection being rebuilt by this daemon right now? True for a rebuild
    /// of the shard's own tenant and for a rebuild of every tenant. While it is, the daemon refuses to start
    /// the shard (<see cref="ShardStartFailureReason.Rebuilding" />), so a caller sweeping for stopped shards
    /// to restart should skip it. Daemons that do not track rebuilds answer false.
    /// </summary>
    bool IsRebuilding(ShardName shardName) => false;

    /// <summary>
    /// jasperfx#1201: rebuild one identity of a single-stream projection inside the shard that runs it on this
    /// daemon, without stopping the shard. The rebuild is bound to the next batch the shard builds, which re-folds
    /// the identity from nothing over its stream up to the batch's ceiling, through the projection's own write path
    /// and with side effects suppressed, and commits it with the shard's progression. The task completes when that
    /// batch commits.
    /// </summary>
    /// <param name="projectionName">The projection's name</param>
    /// <param name="tenantId">The tenant the identity belongs to, or null for the default tenant</param>
    /// <param name="identity">The projected document's identity</param>
    /// <param name="token"></param>
    /// <exception cref="IdentityRebuildRefusedException">
    /// The projection is not a single-stream aggregation, its shard is not running here, is rebuilding, or has
    /// never committed, or the store does not support it.
    /// </exception>
    Task<IdentityRebuildResult> RebuildIdentityAsync(string projectionName, string? tenantId, object identity,
        CancellationToken token)
        => RebuildIdentityAsync(projectionName,
            new IdentityRebuildRequest(tenantId ?? StorageConstants.DefaultTenantId, identity), token);

    /// <summary>
    /// jasperfx#1201: <see cref="RebuildIdentityAsync(string, string?, object, CancellationToken)" />, with a
    /// callback that writes in the same batch and one that runs after it commits.
    /// </summary>
    Task<IdentityRebuildResult> RebuildIdentityAsync(string projectionName, IdentityRebuildRequest request,
        CancellationToken token)
        => throw new NotSupportedException($"{GetType().FullName} does not support per-identity projection rebuilds.");

    /// <summary>
    /// marten#6003: rebuild one identity of a projection whose identities can only be found by scanning
    /// (<see cref="Projections.IdentityAccessClass.ScanOnly" />), or of any aggregation projection: read every event of
    /// the projection's types from the start up to the shard's committed position off the shard's loop, slice each page
    /// and keep the identity's events, then bind the rest to the next batch the way
    /// <see cref="RebuildIdentityAsync(string, string?, object, CancellationToken)" /> does. Unparks the identity if it
    /// was parked. This reads the whole event store's worth of the projection's events, so it is logged as a warning
    /// and reports <paramref name="progress" />; turn on the event-type index if your store has one. Cancelling it
    /// before it binds changes nothing.
    /// </summary>
    Task<IdentityRebuildResult> RebuildIdentityByScanAsync(string projectionName, string? tenantId, object identity,
        IProgress<Projections.IdentityScanProgress>? progress, CancellationToken token)
        => throw new NotSupportedException($"{GetType().FullName} does not support per-identity projection rebuilds.");

    /// <summary>
    /// marten#6003: the identities of a projection that are parked in this daemon's database, oldest first, with the
    /// error that parked each one.
    /// </summary>
    /// <param name="projectionName">The projection's name</param>
    /// <param name="tenantId">One tenant's parked identities, or null for every tenant's</param>
    /// <param name="token"></param>
    Task<IReadOnlyList<ParkedIdentity>> FetchParkedIdentitiesAsync(string projectionName, string? tenantId,
        CancellationToken token)
        => throw new NotSupportedException($"{GetType().FullName} does not park projection identities.");

    /// <summary>
    /// marten#6003: replay one parked identity in the shard that runs it on this daemon: re-fold it from nothing up
    /// to the next batch's ceiling, fire the side effects of its events above the point where it parked, and unpark
    /// it, all in that batch. If the re-fold fails again, the identity stays parked and the result says
    /// <see cref="ParkedReplayOutcome.FailedAgain" />. From another process, use
    /// <see cref="IEventDatabase.RequestParkedActionAsync" /> instead, which the node that runs the shard picks up.
    /// </summary>
    /// <exception cref="IdentityRebuildRefusedException">The shard is not running on this daemon.</exception>
    Task<ParkedReplayResult> ReplayParkedIdentityAsync(string projectionName, string? tenantId, object identity,
        CancellationToken token)
        => throw new NotSupportedException($"{GetType().FullName} does not park projection identities.");

    /// <summary>
    /// marten#6003: replay every parked identity of a projection that this daemon runs, or the oldest few, at most
    /// <see cref="ErrorHandlingOptions.ParkedReplayBatchSize" /> of them to a batch.
    /// </summary>
    Task<IReadOnlyList<ParkedReplayResult>> ReplayParkedIdentitiesAsync(string projectionName, string? tenantId,
        ParkedSelection selection, CancellationToken token)
        => throw new NotSupportedException($"{GetType().FullName} does not park projection identities.");

    /// <summary>
    /// marten#6003: discard one parked identity: re-fold it, dropping and dead-lettering each event that still fails,
    /// and unpark it. Nothing is lost beyond what a skip would have lost.
    /// </summary>
    /// <exception cref="IdentityRebuildRefusedException">The shard is not running on this daemon.</exception>
    Task<ParkedReplayResult> DiscardParkedIdentityAsync(string projectionName, string? tenantId, object identity,
        CancellationToken token)
        => throw new NotSupportedException($"{GetType().FullName} does not park projection identities.");

    /// <summary>
    /// marten#6003: is this identity parked in a shard this daemon runs? False when the shard isn't running here.
    /// Late-event recovery uses it to leave a parked identity to its replay.
    /// </summary>
    bool IsIdentityParked(string projectionName, string? tenantId, object identity) => false;

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
    /// <remarks>
    /// Under per-tenant event partitioning this rewinds the subscription's own shards for each tenant whose
    /// agent this daemon runs. Each tenant has its own event sequence there, so a non-zero
    /// <paramref name="sequenceFloor" /> identifies a position in one tenant's events only and is refused with an
    /// <see cref="ArgumentOutOfRangeException" />: rewind that tenant through
    /// <see cref="RewindSubscriptionAsync(string, string?, CancellationToken, long?, DateTimeOffset?)" />.
    /// </remarks>
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
