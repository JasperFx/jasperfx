using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Polly;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Shared leadership-election + agent-lifecycle loop for the projection coordinator.
/// Lifted from the slot-for-slot duplicate that lived in Marten's
/// <c>ProjectionCoordinator.executeAsync</c> and Polecat's
/// <c>ProjectionCoordinator.ExecuteAsync</c> (Polecat's own xmldoc said it
/// "mirrors Marten's executeAsync loop slot-for-slot").
/// </summary>
/// <remarks>
/// Everything the loop touches is already on a lifted contract
/// (<see cref="IProjectionDistributor"/>, <see cref="IProjectionDaemon"/>,
/// <see cref="IProjectionSet"/>, <see cref="ISubscriptionAgent"/>), so the only
/// store-specific seams are: how a database resolves to an
/// <see cref="IProjectionDaemon"/> (kept in the subclass behind
/// <see cref="ResolveDaemon"/> so each store keeps its own daemon cache and
/// concurrency model), and the three <see cref="IProjectionCoordinator"/> daemon
/// accessors which need each store's tenancy/database resolution.
///
/// This lift also normalizes two real divergences (see #326):
/// <list type="bullet">
///   <item>Agent-start resilience — Marten wrapped <c>StartAgentAsync</c> in a
///   <see cref="ResiliencePipeline"/> with eject-paused-shard + release-lock on
///   failure; Polecat called it raw. The shared loop adopts Marten's resilient
///   variant, so Polecat gains lock-release-on-failure.</item>
///   <item>Pause/Resume/Stop bookkeeping — normalized on Marten's single
///   cancellation-source + awaited runner model rather than Polecat's separate
///   paused/cts/task tracking.</item>
/// </list>
///
/// Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public abstract class ProjectionCoordinatorBase : IProjectionCoordinator
{
    private readonly ILogger _logger;
    private readonly ResiliencePipeline _resilience;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leadershipPollingTime;
    private readonly TimeSpan _agentPauseTime;
    private readonly TimeSpan _healthCheckPollingTime;

    private CancellationTokenSource? _cancellation;
    private Task? _runner;

    /// <summary>
    /// The distributor that decides which (database × shards) sets this node should
    /// try to run, and negotiates the per-set leadership locks. Null when the coordinator
    /// is constructed in a "nothing to coordinate" state — see the <see cref="ProjectionCoordinatorBase"/>
    /// ctor remarks.
    /// </summary>
    public IProjectionDistributor? Distributor { get; }

    /// <param name="distributor">
    /// May be null. A coordinator can be legitimately constructed but never started when the
    /// async daemon is <c>Disabled</c>: there is no distribution work, so a store's
    /// <c>BuildDistributor</c> naturally returns null in that mode. In that case
    /// <see cref="StartAsync"/> no-ops and the loop never runs. See
    /// <see href="https://github.com/JasperFx/jasperfx/issues/352">#352</see>.
    /// </param>
    protected ProjectionCoordinatorBase(
        IProjectionDistributor? distributor,
        ILogger logger,
        ResiliencePipeline resilience,
        TimeProvider timeProvider,
        TimeSpan leadershipPollingTime,
        TimeSpan agentPauseTime,
        TimeSpan healthCheckPollingTime)
    {
        Distributor = distributor;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resilience = resilience ?? throw new ArgumentNullException(nameof(resilience));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _leadershipPollingTime = leadershipPollingTime;
        _agentPauseTime = agentPauseTime;
        _healthCheckPollingTime = healthCheckPollingTime;
    }

    /// <summary>
    /// Resolve (and cache, in the subclass) the <see cref="IProjectionDaemon"/> that
    /// runs the shards for the given set's database. Kept in the subclass so each store
    /// keeps its own cache + concurrency model (Marten: ImHashMap + double-checked lock;
    /// Polecat: a plain dictionary under single-threaded execution).
    /// </summary>
    protected abstract IProjectionDaemon ResolveDaemon(IProjectionSet set);

    /// <summary>
    /// A snapshot of the daemons currently resolved/cached by the subclass. Used by the
    /// loop's pause-time heuristic and by Pause/Stop to fan out across every running daemon.
    /// </summary>
    protected abstract IReadOnlyList<IProjectionDaemon> ResolvedDaemons();

    /// <inheritdoc />
    public abstract IProjectionDaemon DaemonForMainDatabase();

    /// <inheritdoc />
    public abstract ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier);

    /// <inheritdoc />
    public abstract ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // No distributor means there is nothing to coordinate (e.g. a Disabled async daemon).
        // The coordinator was constructed but should never spin up its leadership loop. See #352.
        if (Distributor == null)
        {
            return Task.CompletedTask;
        }

        _cancellation?.SafeDispose();

        _cancellation = new CancellationTokenSource();
        _runner = Task.Run(() => executeAsync(_cancellation.Token), _cancellation.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PauseAsync()
    {
        _logger.LogInformation("Pausing ProjectionCoordinator");
        if (_cancellation != null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        await drainRunner().ConfigureAwait(false);

        foreach (var daemon in ResolvedDaemons())
        {
            try
            {
                await daemon.StopAllAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error while trying to stop daemon agents");
            }
        }
    }

    /// <inheritdoc />
    public Task ResumeAsync()
    {
        return StartAsync(default);
    }

    /// <inheritdoc />
    public virtual async Task StopAsync(CancellationToken cancellationToken)
    {
        await PauseAsync().ConfigureAwait(false);

        foreach (var daemon in ResolvedDaemons())
        {
            daemon.SafeDispose();
        }

        var distributor = Distributor;
        if (distributor != null)
        {
            try
            {
                await distributor.ReleaseAllLocks().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to release subscription agent locks");
            }
        }
    }

    private async Task drainRunner()
    {
        if (_runner == null) return;

        try
        {
#pragma warning disable VSTHRD003
            await _runner.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (TaskCanceledException)
        {
            // Nothing, just from shutting down
        }
        catch (OperationCanceledException)
        {
            // Nothing, just from shutting down
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to stop the ProjectionCoordinator");
        }
    }

    private async Task executeAsync(CancellationToken stoppingToken)
    {
        // StartAsync only reaches here with a non-null distributor; capture it locally so the
        // null-state flows cleanly through the loop and the nested start path.
        var distributor = Distributor;
        if (distributor == null) return;

        await distributor.RandomWait(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sets = await distributor.BuildDistributionAsync().ConfigureAwait(false);

                foreach (var set in sets)
                {
                    if (stoppingToken.IsCancellationRequested) return;

                    // Is it already running here?
                    if (distributor.HasLock(set))
                    {
                        var daemon = ResolveDaemon(set);
                        await startAgentsIfNecessaryAsync(distributor, set, daemon, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        if (await distributor.TryAttainLockAsync(set, stoppingToken).ConfigureAwait(false))
                        {
                            var daemon = ResolveDaemon(set);
                            await startAgentsIfNecessaryAsync(distributor, set, daemon, stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // We don't hold the lock, so we might've lost it due to a database
                            // outage. Make sure any agents are no longer running on this node.
                            var daemon = ResolveDaemon(set);
                            await stopAgentsIfNecessaryAsync(set, daemon).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e,
                            "Error trying to attain a lock for set {Name} and lock id {LockId}. Will retry later",
                            set.Names.Select(x => x.Identity).Join(", "), set.LockId);
                        await Task.Delay(_leadershipPollingTime, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                // Only really expect any errors if there are dynamic tenants in place
                _logger.LogError(e, "Error trying to resolve projection distributions");
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (ResolvedDaemons().Any(x => x.HasAnyPaused()))
                {
                    await Task.Delay(_agentPauseTime, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(_leadershipPollingTime, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
                // just get out of here, this signals a graceful shutdown attempt
            }
            catch (OperationCanceledException)
            {
                // Nothing, just from shutting down
            }
        }
    }

    private async Task startAgentsIfNecessaryAsync(IProjectionDistributor distributor, IProjectionSet set,
        IProjectionDaemon daemon, CancellationToken stoppingToken)
    {
        // jasperfx#489: reconcile before starting. Under per-tenant agent distribution the
        // set's Names are re-expanded from the current tenant list on every leadership
        // cycle, so an agent whose identity no longer appears in Names is stale — a
        // removed tenant's agent, or the store-global agent left over from a
        // pre-expansion cycle (the transition edge; same shape as Wolverine's #3328
        // stale-agent retirement). Reap it FIRST so a store-global agent never runs
        // concurrently with the per-tenant agents that replace it. For sets whose Names
        // match the running agents exactly (every non-partitioned store), this finds
        // nothing and the pass is inert.
        await reapOrphanedAgentsAsync(set, daemon).ConfigureAwait(false);

        foreach (var name in set.Names)
        {
            var agent = daemon.CurrentAgents().FirstOrDefault(x => x.Name.Equals(name));
            if (agent == null)
            {
                await tryStartAgent(distributor, stoppingToken, daemon, name, set).ConfigureAwait(false);
            }
            else if (agent is { Status: AgentStatus.Paused, PausedTime: not null } &&
                     _timeProvider.GetUtcNow().Subtract(agent.PausedTime.Value) > _healthCheckPollingTime)
            {
                await tryStartAgent(distributor, stoppingToken, daemon, name, set).ConfigureAwait(false);
            }
        }
    }

    private async Task stopAgentsIfNecessaryAsync(IProjectionSet set, IProjectionDaemon daemon)
    {
        foreach (var shardName in set.Names)
        {
            var status = daemon.StatusFor(shardName.Identity);
            if (status == AgentStatus.Running)
            {
                await daemon.StopAgentAsync(shardName.Identity).ConfigureAwait(false);
            }
        }

        // jasperfx#489: also stop stale agents that belong to this set's store-global
        // shards but whose identity fell out of the freshly expanded Names (a tenant
        // that was removed since this node last held the lock). The identity loop
        // above can't see them because they're no longer in Names.
        await reapOrphanedAgentsAsync(set, daemon).ConfigureAwait(false);
    }

    /// <summary>
    /// jasperfx#489: stop agents currently running for this set's store-global shards
    /// whose identity is NOT in the set's current Names. Identities are matched on the
    /// tenant-neutral base (same store-global shard) so agents belonging to other
    /// projections — or other sets on the same daemon — are never touched.
    /// </summary>
    private async Task reapOrphanedAgentsAsync(IProjectionSet set, IProjectionDaemon daemon)
    {
        var currentIdentities = set.Names.Select(x => x.Identity).ToHashSet();
        var baseIdentities = set.Names.Select(tenantNeutralIdentity).ToHashSet();

        foreach (var agent in daemon.CurrentAgents().ToArray())
        {
            var identity = agent.Name.Identity;
            if (currentIdentities.Contains(identity)) continue;
            if (!baseIdentities.Contains(tenantNeutralIdentity(agent.Name))) continue;

            try
            {
                await daemon.StopAgentAsync(identity).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Error trying to stop orphaned subscription agent {Name} on database {Database}",
                    identity, set.Database.Identifier);
            }
        }
    }

    /// <summary>
    /// The store-global (tenant-neutral) identity for a shard name — the identity with
    /// the trailing tenant slot removed. A store-global name is its own base.
    /// </summary>
    private static string tenantNeutralIdentity(ShardName name)
    {
        return name.TenantId == null
            ? name.Identity
            : ShardName.Compose(name.Name, name.ShardKey, null, name.Version).Identity;
    }

    private async Task tryStartAgent(IProjectionDistributor distributor, CancellationToken stoppingToken,
        IProjectionDaemon daemon, ShardName name, IProjectionSet set)
    {
        try
        {
            await _resilience.ExecuteAsync(
                static (x, t) => new ValueTask(x.Daemon.StartAgentAsync(x.Name.Identity, t)),
                new DaemonShardName(daemon, name), stoppingToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to start subscription {Name} on database {Database}", name.Identity,
                set.Database.Identifier);
            if (daemon.StatusFor(name.Identity) == AgentStatus.Paused)
            {
                daemon.EjectPausedShard(name.Identity);
            }

            await distributor.ReleaseLockAsync(set).ConfigureAwait(false);
        }
    }

    private record DaemonShardName(IProjectionDaemon Daemon, ShardName Name);
}
