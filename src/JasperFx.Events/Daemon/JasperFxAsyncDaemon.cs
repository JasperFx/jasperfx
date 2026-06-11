using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;


[UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): parameter receiving DAM-annotated Type from reflective lookups during shard / agent construction. The projection types are preserved at the registered projection boundary on the caller side.")]
public partial class JasperFxAsyncDaemon<TOperations, TQuerySession, TProjection> : IObserver<ShardState>, IDaemonRuntime
    where TOperations : TQuerySession, IStorageOperations
    where TProjection : IJasperFxProjection<TOperations>
{
    private readonly IEventStore<TOperations, TQuerySession> _store;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ProjectionGraph<TProjection, TOperations, TQuerySession> _projections;
    private ImHashMap<string, ISubscriptionAgent> _agents = ImHashMap<string, ISubscriptionAgent>.Empty;
    private CancellationTokenSource _cancellation = new();
    private readonly HighWaterAgent _highWater;
    private readonly IDisposable _breakSubscription;
    private RetryBlock<DeadLetterEvent> _deadLetterBlock;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    // Only non-null when the backing store partitions events per tenant; null keeps the daemon on the
    // single store-global high-water mark (today's behavior, byte for byte). jasperfx#407 Phase 2b.
    private readonly TenantedHighWaterCoordinator? _tenantHighWater;

    public JasperFxAsyncDaemon(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory, IHighWaterDetector detector, ProjectionGraph<TProjection, TOperations, TQuerySession> projections)
    {
        Database = database;
        _store = store;
        _loggerFactory = loggerFactory;
        _projections = projections;
        Logger = loggerFactory.CreateLogger(GetType());
        Tracker = Database.Tracker;
        _highWater = new HighWaterAgent(store.Meter, detector, Tracker, loggerFactory.CreateLogger<HighWaterAgent>(), projections, _cancellation.Token);

        if (detector.SupportsTenantPartitioning)
        {
            _tenantHighWater = new TenantedHighWaterCoordinator(detector, loggerFactory.CreateLogger<TenantedHighWaterCoordinator>());
        }

        _breakSubscription = database.Tracker.Subscribe(this);

        _deadLetterBlock = buildDeadLetterBlock();
    }

    public JasperFxAsyncDaemon(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger, IHighWaterDetector detector, ProjectionGraph<TProjection, TOperations, TQuerySession> projections)
    {
        Database = database;
        _store = store;
        _projections = projections;
        _loggerFactory = null;
        Logger = logger;
        Tracker = Database.Tracker;
        _highWater = new HighWaterAgent(store.Meter, detector, Tracker, logger, _projections, _cancellation.Token);

        if (detector.SupportsTenantPartitioning)
        {
            _tenantHighWater = new TenantedHighWaterCoordinator(detector, logger);
        }

        _breakSubscription = database.Tracker.Subscribe(this);

        _deadLetterBlock = buildDeadLetterBlock();
    }

    private RetryBlock<DeadLetterEvent> buildDeadLetterBlock()
    {
        return new RetryBlock<DeadLetterEvent>(async (deadLetterEvent, token) =>
        {
            // More important to end cleanly
            if (token.IsCancellationRequested) return;

            await Database.StoreDeadLetterEventAsync(_store, deadLetterEvent, token).ConfigureAwait(false);
        }, Logger, _cancellation.Token);
    }

    public IEventDatabase Database { get; }

    public ILogger Logger { get; }

    public void Dispose()
    {
        _cancellation?.Dispose();
        _highWater?.Dispose();
        _breakSubscription.Dispose();
        _deadLetterBlock.Dispose();
    }

    public ShardStateTracker Tracker { get; }

    /// <summary>
    /// JasperFx/ProductSupport#5 — Subject URI of the
    /// <see cref="IEventStore"/> this daemon was built against. Consumed by
    /// <see cref="ProjectionDaemonExtensions.SubscribeWithStoreUriStamp"/>
    /// to stamp <see cref="ShardState.StoreUri"/> on every state the daemon
    /// publishes through the shared <see cref="Tracker"/>.
    /// </summary>
    public string? StoreUri => _store.Subject?.ToString();

    public bool IsRunning => _highWater.IsRunning;


    private async Task<bool> tryStartAgentAsync(ISubscriptionAgent agent, ShardExecutionMode mode)
    {
        // Be idempotent, don't start an agent that is already running
        if (_agents.TryFind(agent.Name.Identity, out var running) && running.Status == AgentStatus.Running)
        {
            return false;
        }

        // Lock
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            // Be idempotent, don't start an agent that is already running now that we have the lock.
            if (_agents.TryFind(agent.Name.Identity, out running) && running.Status == AgentStatus.Running)
            {
                return false;
            }

            var highWaterMark = HighWaterMark();

            // marten#4717: a tenant-scoped continuous agent must advance against its OWN tenant's
            // high-water, not the store-global mark — each tenant's seq_id is independent, so seeding a
            // tenant agent with the global mark makes it over-run to the max tenant's height. StartAllAsync
            // primes the per-tenant ceilings before starting agents; fall back to 0 until first polled.
            if (_tenantHighWater != null && agent.Name.TenantId != null)
            {
                highWaterMark = _tenantHighWater.CeilingFor(agent.Name.TenantId) ?? 0L;
            }

            var position = await agent
                .Options
                .DetermineStartingPositionAsync(highWaterMark, agent.Name, mode, Database, _cancellation.Token)
                .ConfigureAwait(false);

            if (position.ShouldUpdateProgressFirst)
            {
                await _store.RewindSubscriptionProgressAsync(Database, agent.Name.Identity, _cancellation.Token, position.Floor).ConfigureAwait(false);
            }

            var errorOptions = mode == ShardExecutionMode.Continuous
                ? _store.ContinuousErrors
                : _store.RebuildErrors;

            var request = new SubscriptionExecutionRequest(position.Floor, mode, errorOptions, this);
            if (_tenantHighWater != null && agent.Name.TenantId != null)
            {
                // marten#4717: seed the tenant agent's ceiling from its own high-water. The agent's
                // high-water can only be raised after start (a lower MarkHighWater is ignored), so this
                // must be passed at start or the agent over-runs to the store-global mark.
                request = request with { StartingHighWater = highWaterMark };
            }

            await agent.StartAsync(request).ConfigureAwait(false);
            agent.MarkHighWater(highWaterMark);

            _agents = _agents.AddOrUpdate(agent.Name.Identity, agent);
            syncTenantPolling();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error trying to start agent {ShardName}", agent.Name.Identity);
            return false;
        }
        finally
        {
            _semaphore.Release();
        }

        return true;
    }

    private async Task rebuildAgent(ISubscriptionAgent agent, long highWaterMark, TimeSpan shardTimeout)
    {
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            // Ensure that the agent is stopped if it is already running
            await stopIfRunningAsync(agent.Name.Identity).ConfigureAwait(false);

            var errorOptions = _store.RebuildErrors;

            var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Rebuild, errorOptions, this);
            await agent.ReplayAsync(request, highWaterMark, shardTimeout).ConfigureAwait(false);

            _agents = _agents.AddOrUpdate(agent.Name.Identity, agent);
        }
        finally
        {
            _semaphore.Release();
        }
    }
    
    
    public async Task StartAgentAsync(string shardName, CancellationToken token)
    {
        if (!_highWater.IsRunning)
        {
            await StartHighWaterDetectionAsync().ConfigureAwait(false);
        }

        // TODO -- DO NOT LIKE THIS. Would rather have an overload that takes ShardName now
        if (!shardName.Contains(":"))
        {
            var shardNames = _store.AllShards().Where(x => x.Name.Name.EqualsIgnoreCase(shardName)).ToArray();
            if (shardNames.Any())
            {
                foreach (var name in shardNames)
                {
                    await StartAgentAsync(name.Name.Identity, token).ConfigureAwait(false);
                }

                return;
            }
        }


        var shard = _store.AllShards().FirstOrDefault(x => x.Name.Identity == shardName);
        if (shard == null)
        {
            throw new ArgumentOutOfRangeException(nameof(shardName),
                $"Unknown shard name '{shardName}'. Value options are {_store.AllShards().Select(x => x.Name.Identity).Join(", ")}");
        }

        var agent = buildAgentForShard(shard);

        var didStart = await tryStartAgentAsync(agent, ShardExecutionMode.Continuous).ConfigureAwait(false);

        if (!didStart && agent is IAsyncDisposable d)
        {
            // Could not be started
            await d.DisposeAsync().ConfigureAwait(false);
        }
    }
    
    public async Task<ISubscriptionAgent> StartAgentAsync(ShardName name, CancellationToken token)
    {
        await StartAgentAsync(name.Identity, token);
        if (_agents.TryFind(name.Identity, out var agent)) return agent;

        // Should not ever happen, but real life man
        throw new Exception("Unable to start a subscription agent for " + name);
    }

    public Task StopAgentAsync(ShardName shardName, Exception? ex = null)
    {
        return StopAgentAsync(shardName.Identity);
    }

    private SubscriptionAgent buildAgentForShard(AsyncShard<TOperations, TQuerySession> shard)
    {
        var execution = _loggerFactory == null ? shard.Factory.BuildExecution(_store, Database, Logger, shard.Name) : shard.Factory.BuildExecution(_store, Database, _loggerFactory, shard.Name);
        var loader = _store.BuildEventLoader(Database, Logger, shard.Filters, shard.Options, shard.Name);

        var metrics = new SubscriptionMetrics(_store, shard.Name, Database);
        
        var agent = new SubscriptionAgent(shard.Name, shard.Options, _store.TimeProvider, loader, execution,
            Database.Tracker, metrics, _loggerFactory?.CreateLogger<SubscriptionAgent>() ?? Logger);
        
        return agent;
    }

    private async Task stopIfRunningAsync(string shardIdentity)
    {
        if (_agents.TryFind(shardIdentity, out var agent))
        {
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(5.Seconds());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _cancellation.Token);

            try
            {
                await agent.StopAndDrainAsync(linked.Token).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop and drain a subscription agent for '{Name}'",
                    agent.Name.Identity);
            }
            finally
            {
                _agents = _agents.Remove(shardIdentity);
                syncTenantPolling();
            }
        }
    }

    public async Task StopAgentAsync(string shardName, Exception? ex = null)
    {
        if (_agents.TryFind(shardName, out var agent))
        {
            await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            try
            {
                var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(5.Seconds());
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _cancellation.Token);

                try
                {
                    await agent.StopAndDrainAsync(linked.Token).ConfigureAwait(true);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error trying to stop and drain a subscription agent for '{Name}'",
                        agent.Name.Identity);
                }
                finally
                {
                    _agents = _agents.Remove(shardName);
                    syncTenantPolling();

                    if (!_agents.Enumerate().Any() && _highWater.IsRunning)
                    {
                        // Nothing happening, so might as well stop hammering the database!
                        await _highWater.StopAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    public async Task StartAllAsync()
    {
        if (!_highWater.IsRunning)
        {
            await StartHighWaterDetectionAsync().ConfigureAwait(false);
        }

        var agents = new List<ISubscriptionAgent>();

        if (_tenantHighWater != null && Database is ICrossTenantRebuildSource crossTenantSource)
        {
            // marten#4717: under per-tenant event partitioning each tenant's events draw seq_id from its
            // own mt_events_sequence_{suffix} starting at 1, so a single store-global <Projection>:All
            // shard cannot track multiple tenants. Fan out one continuous agent per (shard, tenant) —
            // exactly the shape catchUpPerTenantAsync / rebuildProjectionForTenant already use — so each
            // tenant's projection advances against its own high-water and persists its own
            // <Projection>:All:<tenant> progression row. OnNext + pollTenantHighWaterAsync already route
            // each tenant's mark to its TenantId-bearing agents.
            await buildPerTenantContinuousAgents(crossTenantSource, agents).ConfigureAwait(false);

            // Prime the per-tenant ceilings BEFORE starting the agents so each tenant agent seeds from
            // its own high-water (tryStartAgentAsync reads CeilingFor) rather than the store-global mark.
            // PollAsync populates the monitor's ceilings directly from pg_sequences, independent of the
            // store-global high-water agent, so the readings are available even pre-start.
            await pollTenantHighWaterAsync().ConfigureAwait(false);
        }
        else
        {
            foreach (var shard in _store.AllShards())
            {
                agents.Add(buildAgentForShard(shard));
            }
        }

        foreach (var agent in agents)
        {
            await tryStartAgentAsync(agent, ShardExecutionMode.Continuous).ConfigureAwait(false);
        }
    }

    // marten#4717: build one continuous agent per (shard, tenant), enumerating tenants from the store's
    // ICrossTenantRebuildSource (mt_tenant_partitions). A projection with no registered tenants yet keeps
    // its store-global agent so it still runs (there are no events to process until a tenant exists).
    private async Task buildPerTenantContinuousAgents(
        ICrossTenantRebuildSource crossTenantSource, List<ISubscriptionAgent> agents)
    {
        foreach (var shard in _store.AllShards())
        {
            var tenants = await crossTenantSource
                .FindRebuildTenantsAsync(shard.Name.Name, _cancellation.Token).ConfigureAwait(false);

            if (tenants.Count == 0)
            {
                agents.Add(buildAgentForShard(shard));
                continue;
            }

            foreach (var tenantId in tenants)
            {
                _tenantHighWater!.PolledTenants.Activate(tenantId);
                var tenantShard = shard with { Name = shard.Name.ForTenant(tenantId) };
                agents.Add(buildAgentForShard(tenantShard));
            }
        }
    }

    public async Task StopAllAsync()
    {
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            await _highWater.StopAsync().ConfigureAwait(false);

            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(5.Seconds());
            try
            {
                var activeAgents = _agents.Enumerate().Select(x => x.Value).Where(x => x.Status == AgentStatus.Running)
                    .ToArray();
                await Parallel.ForEachAsync(activeAgents, cancellation.Token,
                    (agent, t) => new ValueTask(agent.StopAndDrainAsync(t))).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Nothing, you're already trying to get out
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop subscription agents for {Agents}", _agents.Enumerate().Select(x => x.Value.Name.Identity).Join(", "));
            }

            try
            {
                await _deadLetterBlock.DrainAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to finish all outstanding DeadLetterEvent persistence");
            }

            _agents = ImHashMap<string, ISubscriptionAgent>.Empty;
            syncTenantPolling();

            _cancellation.TryReset();
            _deadLetterBlock = buildDeadLetterBlock();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task StartHighWaterDetectionAsync()
    {
        if (_store.AutoCreateSchemaObjects != AutoCreate.None)
        {
            await Database.EnsureStorageExistsAsync(typeof(IEvent), _cancellation.Token).ConfigureAwait(false);
        }

        await _highWater.StartAsync().ConfigureAwait(false);
    }

    private ConcurrentBag<ShardState>? _shardStateTracker;
    
    public async Task WaitForNonStaleData(TimeSpan timeout)
    {
        _shardStateTracker = new ConcurrentBag<ShardState>();
        
        try
        {
            await Database.WaitForNonStaleProjectionDataAsync(timeout);
        }
        catch (TimeoutException e)
        {
            var exceptions = _shardStateTracker.Select(x => x.Exception).Where(x => x != null).ToArray();
            if (exceptions.Any())
            {
                throw new AggregateException([e, ..exceptions!]);
            }

            throw;
        }
        finally
        {
            _shardStateTracker = null;
        }
        
    }

    public Task WaitForShardToBeRunning(string shardName, TimeSpan timeout)
    {
        if (StatusFor(shardName) == AgentStatus.Running) return Task.CompletedTask;

        Func<ShardState, bool> match = state =>
        {
            if (!state.ShardName.EqualsIgnoreCase(shardName)) return false;

            return state.Action == ShardAction.Started || state.Action == ShardAction.Updated;
        };

        return Tracker.WaitForShardCondition(match, $"Wait for '{shardName}' to be running",timeout);
    }

    public AgentStatus StatusFor(string shardName)
    {
        if (_agents.TryFind(shardName, out var agent))
        {
            return agent.Status;
        }

        return AgentStatus.Stopped;
    }

    public IReadOnlyList<ISubscriptionAgent> CurrentAgents()
    {
        return _agents.Enumerate().Select(x => x.Value).ToList();
    }

    public bool HasAnyPaused()
    {
        return CurrentAgents().Any(x => x.Status == AgentStatus.Paused);
    }

    public void EjectPausedShard(string shardName)
    {
        // Not worried about a lock here.
        _agents = _agents.Remove(shardName);
    }

    public Task PauseHighWaterAgentAsync()
    {
        return _highWater.StopAsync();
    }

    public long HighWaterMark()
    {
        return Tracker.HighWaterMark;
    }

    void IObserver<ShardState>.OnCompleted()
    {
        // Nothing
    }

    void IObserver<ShardState>.OnError(Exception error)
    {
        // Nothing
    }

    void IObserver<ShardState>.OnNext(ShardState value)
    {
        if (value.ShardName == ShardState.HighWaterMark)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("Event high water mark detected at {Sequence}", value.Sequence);
            }

            foreach (var agent in CurrentAgents())
            {
                // When the store partitions per tenant, tenant-scoped agents advance against their own
                // tenant's high-water mark (routed by the coordinator), not the store-global mark. With no
                // coordinator this is exactly the original behavior — every agent gets the global mark.
                if (_tenantHighWater != null && agent.Name.TenantId != null)
                {
                    continue;
                }

                agent.MarkHighWater(value.Sequence);
            }

            if (_tenantHighWater != null)
            {
                // Reuse the global high-water cadence to drive one vectorized per-tenant poll.
                _ = pollTenantHighWaterAsync();
            }
        }

        _shardStateTracker?.Add(value);
    }

    private async Task pollTenantHighWaterAsync()
    {
        if (_tenantHighWater == null)
        {
            return;
        }

        try
        {
            await _tenantHighWater.PollAndRouteAsync(CurrentAgents(), _cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Error polling per-tenant high water for database {Name}", Database.Identifier);
        }
    }

    // Keep the vectorized monitor's polled-tenant set in step with the shards currently assigned to this
    // node. No-op for non-partitioned stores. jasperfx#407 Phase 2b.
    private void syncTenantPolling()
    {
        _tenantHighWater?.SyncAssignedTenants(CurrentAgents().Select(x => x.Name));
    }

    public Task RecordDeadLetterEventAsync(DeadLetterEvent @event)
    {
        return _deadLetterBlock.PostAsync(@event);
    }


    public Task RebuildProjectionAsync(string projectionName, CancellationToken token)
    {
        return RebuildProjectionAsync(projectionName, 5.Minutes(), token);
    }

    public Task RebuildProjectionAsync<TView>(CancellationToken token)
    {
        return RebuildProjectionAsync<TView>(5.Minutes(), token);
    }

    public Task RebuildProjectionAsync(Type projectionType, CancellationToken token)
    {
        return RebuildProjectionAsync(projectionType, 5.Minutes(), token);
    }

    // projectionType can be either the IProjectionSource type, or the aggregate type
    public Task RebuildProjectionAsync(Type projectionType, TimeSpan shardTimeout, CancellationToken token)
    {
        
        var projection = _projections.All.FirstOrDefault(x => x.GetType() == projectionType)
                         ?? _projections.All.FirstOrDefault(x => x.PublishedTypes().Contains(projectionType))
                         ?? _projections.All.FirstOrDefault(x => x is ProjectionWrapper<TOperations, TQuerySession> wrapper && wrapper.ProjectionType == projectionType);

        if (projection == null && projectionType.CanBeCastTo<IProjectionSource<TOperations, TQuerySession>>() &&
            projectionType.HasDefaultConstructor())
        {
            projection = (IProjectionSource<TOperations, TQuerySession>?)Activator.CreateInstance(projectionType);
        }

        if (projection != null)
        {
            return rebuildProjection(projection, shardTimeout, token);
        }

        throw new ArgumentOutOfRangeException("TView",
            $"No registered projection matches the type '{projectionType.FullNameInCode()} or is known to publish that type'. Available projections are {_projections.All.Select(x => x.ToString()!).Join(", ")}");
    }

    public Task RebuildProjectionAsync(string projectionName, TimeSpan shardTimeout, CancellationToken token)
    {
        if (_projections.TryFindProjection(projectionName, out var source))
        {
            return rebuildProjection(source, shardTimeout, token);
        }
        
        throw new ArgumentOutOfRangeException(nameof(projectionName),
        $"No registered projection matches the name '{projectionName}'. Available names are {_projections.AllProjectionNames().Join(", ")}");
    }

    public Task RebuildProjectionAsync<TView>(TimeSpan shardTimeout, CancellationToken token)
    {
        var projectionType = typeof(TView);
        return RebuildProjectionAsync(projectionType, shardTimeout, token);
    }

    public Task RebuildProjectionAsync(string projectionName, string? tenantId, CancellationToken token)
    {
        return RebuildProjectionAsync(projectionName, tenantId, 5.Minutes(), token);
    }

    // jasperfx#407 Phase 2b: a real per-tenant rebuild. A null tenant is the store-global rebuild
    // (today's behavior). A non-null tenant rebuilds ONLY that tenant's shard up to that tenant's
    // high-water ceiling, pausing only that shard so other tenants keep running.
    public Task RebuildProjectionAsync(string projectionName, string? tenantId, TimeSpan shardTimeout,
        CancellationToken token)
    {
        if (tenantId == null)
        {
            return RebuildProjectionAsync(projectionName, shardTimeout, token);
        }

        if (_projections.TryFindProjection(projectionName, out var source))
        {
            return rebuildProjectionForTenant(source, tenantId, shardTimeout, token);
        }

        throw new ArgumentOutOfRangeException(nameof(projectionName),
            $"No registered projection matches the name '{projectionName}'. Available names are {_projections.AllProjectionNames().Join(", ")}");
    }

    private async Task rebuildProjection(IProjectionSource<TOperations, TQuerySession> source, TimeSpan shardTimeout, CancellationToken token)
    {
        await Database.EnsureStorageExistsAsync(typeof(IEvent), token).ConfigureAwait(false);

        var subscriptionName = source.Name;
        Logger.LogInformation("Starting to rebuild Projection {ProjectionName}@{DatabaseIdentifier}",
            subscriptionName, Database.Identifier);

        await stopRunningAgents(subscriptionName).ConfigureAwait(false);

        if (token.IsCancellationRequested) return;

        // Check now regardless
        await _highWater.CheckNowAsync().ConfigureAwait(false);

        // If there's no data, do nothing
        if (Tracker.HighWaterMark == 0)
        {
            Logger.LogInformation("Aborting projection rebuild because the high water mark is 0 (no event data)");
            return;
        }

        if (token.IsCancellationRequested) return;

        var agents = buildAgentsForSubscription(source);
        if (agents.Count == 0)
        {
            throw new InvalidOperationException("No agents were built for subscription " + subscriptionName);
        }

        foreach (var agent in agents)
        {
            Tracker.MarkAsRestarted(agent.Name);
        }

        // Tear down the current state
        await _store.TeardownExistingProjectionStateAsync(Database, subscriptionName, token).ConfigureAwait(false);

        if (token.IsCancellationRequested)
        {
            return;
        }

        var mark = Tracker.HighWaterMark;

        // Is the shard count the optimal DoP here?
        await Parallel.ForEachAsync(agents,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = agents.Count() },
            async (agent, cancellationToken) =>
            {
                Tracker.MarkAsRestarted(agent.Name);

                await rebuildAgent(agent, mark, shardTimeout).ConfigureAwait(false);
            }).ConfigureAwait(false);

        foreach (var agent in agents)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(shardTimeout);

            try
            {
                await agent.StopAndDrainAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop and drain agent {Name} after rebuilding", agent.Name.Identity);
            }
        }

        if (source.Lifecycle == ProjectionLifecycle.Inline)
        {
            // Tear down the current state
            await _store.DeleteProjectionProgressAsync(Database, subscriptionName, token).ConfigureAwait(false);
        }
    }

    // jasperfx#407 Phase 2b: rebuild a single tenant's shard(s) for one projection, in isolation. Reuses
    // the existing buildAgentForShard / rebuildAgent paths, scoped to ShardName.ForTenant(tenantId).
    private async Task rebuildProjectionForTenant(IProjectionSource<TOperations, TQuerySession> source,
        string tenantId, TimeSpan shardTimeout, CancellationToken token)
    {
        await Database.EnsureStorageExistsAsync(typeof(IEvent), token).ConfigureAwait(false);

        var subscriptionName = source.Name;
        Logger.LogInformation("Starting to rebuild Projection {ProjectionName} for tenant {TenantId}@{DatabaseIdentifier}",
            subscriptionName, tenantId, Database.Identifier);

        // Stop ONLY this tenant's shards for this projection; every other tenant keeps running.
        await stopRunningAgentsForTenant(subscriptionName, tenantId).ConfigureAwait(false);

        if (token.IsCancellationRequested) return;

        // Per-tenant rebuild ceiling = that tenant's high-water mark, looked up from the vectorized
        // monitor. Falls back to the store-global mark until the monitor has a reading for the tenant.
        long ceiling;
        if (_tenantHighWater != null)
        {
            _tenantHighWater.PolledTenants.Activate(tenantId);
            await pollTenantHighWaterAsync().ConfigureAwait(false);
            ceiling = _tenantHighWater.CeilingFor(tenantId) ?? Tracker.HighWaterMark;
        }
        else
        {
            await _highWater.CheckNowAsync().ConfigureAwait(false);
            ceiling = Tracker.HighWaterMark;
        }

        if (ceiling == 0)
        {
            Logger.LogInformation(
                "Aborting tenant rebuild of {ProjectionName}/{TenantId} because the high water mark is 0 (no event data)",
                subscriptionName, tenantId);
            return;
        }

        if (token.IsCancellationRequested) return;

        var agents = buildTenantAgentsForSubscription(source, tenantId);
        if (agents.Count == 0)
        {
            throw new InvalidOperationException(
                $"No agents were built for subscription {subscriptionName} and tenant {tenantId}");
        }

        foreach (var agent in agents)
        {
            Tracker.MarkAsRestarted(agent.Name);
        }

        // Reset ONLY this tenant's progression rows. The tenant-scoped document teardown is performed by
        // the store's tenant-aware rebuild execution (keyed on ShardName.TenantId). We intentionally do
        // NOT call the store-global TeardownExistingProjectionStateAsync here — that would wipe every
        // other tenant's data.
        await _store.DeleteProjectionProgressAsync(Database, subscriptionName, tenantId, token).ConfigureAwait(false);

        if (token.IsCancellationRequested) return;

        await Parallel.ForEachAsync(agents,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = agents.Count },
            async (agent, _) =>
            {
                Tracker.MarkAsRestarted(agent.Name);
                await rebuildAgent(agent, ceiling, shardTimeout).ConfigureAwait(false);
            }).ConfigureAwait(false);

        foreach (var agent in agents)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(shardTimeout);

            try
            {
                await agent.StopAndDrainAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop and drain tenant rebuild agent {Name}", agent.Name.Identity);
            }
        }
    }

    private IReadOnlyList<SubscriptionAgent> buildTenantAgentsForSubscription(
        ISubscriptionSource<TOperations, TQuerySession> source, string tenantId)
    {
        var agents = new List<SubscriptionAgent>();

        foreach (var shard in source.Shards())
        {
            // Rebind the shard identity to the tenant slot so the store builds a tenant-scoped execution
            // and progression key.
            var tenantShard = shard with { Name = shard.Name.ForTenant(tenantId) };
            agents.Add(buildAgentForShard(tenantShard));
        }

        return agents;
    }

    private async Task stopRunningAgentsForTenant(string subscriptionName, string tenantId)
    {
        var running = CurrentAgents()
            .Where(x => x.Name.Name == subscriptionName && x.Name.TenantId == tenantId)
            .ToArray();

        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            foreach (var agent in running)
            {
                await agent.HardStopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task stopRunningAgents(string subscriptionName)
    {
        var running = CurrentAgents().Where(x => x.Name.Name == subscriptionName).ToArray();

        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            foreach (var agent in running)
            {
                await agent.HardStopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }


    public async Task PrepareForRebuildsAsync()
    {
        if (_highWater.IsRunning)
        {
            await _highWater.StopAsync().ConfigureAwait(false);
        }

        await _highWater.CheckNowAsync().ConfigureAwait(false);
    }

    public async Task RewindSubscriptionAsync(string subscriptionName, CancellationToken token, long? sequenceFloor = 0,
        DateTimeOffset? timestamp = null)
    {
        if (timestamp.HasValue)
        {
            sequenceFloor = await Database.FindEventStoreFloorAtTimeAsync(timestamp.Value, token).ConfigureAwait(false);
            if (sequenceFloor == null) return;
        }

        if (_cancellation.IsCancellationRequested) return;

        await stopRunningAgents(subscriptionName).ConfigureAwait(false);

        if (_cancellation.IsCancellationRequested) return;

        await _store.RewindSubscriptionProgressAsync(Database, subscriptionName, token, sequenceFloor).ConfigureAwait(false);

        var agents = buildAgentsForSubscription(subscriptionName);
        
        foreach (var agent in agents)
        {
            Tracker.MarkAsRestarted(agent.Name);
            var errorOptions = _store.RebuildErrors;
            await agent.StartAsync(new SubscriptionExecutionRequest(sequenceFloor!.Value, ShardExecutionMode.Continuous,
                errorOptions, this)).ConfigureAwait(false);
            agent.MarkHighWater(HighWaterMark());
        }
    }
    
    private IReadOnlyList<SubscriptionAgent> buildAgentsForSubscription(ISubscriptionSource<TOperations, TQuerySession> source)
    {
        var agents = new List<SubscriptionAgent>();

        foreach (var shard in source.Shards())
        {
            agents.Add(buildAgentForShard(shard));
        }

        return agents;
    }

    private IReadOnlyList<SubscriptionAgent> buildAgentsForSubscription(string subscriptionName)
    {
        var agents = new List<SubscriptionAgent>();

        foreach (var shard in _store.AllShards().Where(x => x.Name.Name.EqualsIgnoreCase(subscriptionName)))
        {
            agents.Add(buildAgentForShard(shard));
        }

        return agents;
    }
    
    public async Task CatchUpAsync(CancellationToken cancellation)
    {
        await StopAllAsync();

        var recorder = new Recorder();
        using var subscription = Database.Tracker.Subscribe(recorder);

        if (_tenantHighWater != null && Database is ICrossTenantRebuildSource crossTenantSource)
        {
            // marten#4665 — under per-tenant event partitioning the store-global
            // mt_events_sequence is never advanced (per-tenant mt_events_sequence_{suffix}
            // values power mt_events.seq_id), so _highWater.CheckNowAsync() leaves the
            // global high-water pinned at the unused sequence's last_value. Driving
            // catch-up off HighWaterMark() in that mode leaves every catch-up loop
            // stuck at zero — the test-automation helper
            // ForceAllMartenDaemonActivityToCatchUpAsync would return "success" with
            // every async projection still behind. Fan out per tenant exactly the way
            // rebuildProjectionForTenant already does: activate every known tenant in
            // the polled set, drive one vectorized poll to fetch ceilings, and catch
            // up a tenant-scoped agent per (shard, tenant) pair to that tenant's
            // ceiling. Falls back to the global path below when no cross-tenant
            // source is available so single-tenant stores stay byte-for-byte.
            await catchUpPerTenantAsync(crossTenantSource, recorder, cancellation).ConfigureAwait(false);
            return;
        }

        await _highWater.CheckNowAsync();

        var progress = await Database.AllProjectionProgress(cancellation);

        foreach (var asyncShard in _store.AllShards())
        {
            var state = progress.FirstOrDefault(x => x.ShardName == asyncShard.Name.Identity)
                        ?? new ShardState(asyncShard.Name, 0);
            var agent = buildAgentForShard(asyncShard);

            await agent.CatchUpAsync(HighWaterMark(), state, cancellation);
            throwIfRecordedExceptions(recorder, cancellation);
        }
    }

    // marten#4665 — per-tenant fan-out for the test-automation catch-up path.
    // Mirrors the rebuildProjectionForTenant ceiling-lookup pattern: activate the
    // tenant in the polled set, drive one vectorized poll, read CeilingFor(tenant).
    // We batch all activations + a single poll per shard so the cost is one
    // round-trip-per-shard against pg_sequences, not one per tenant.
    private async Task catchUpPerTenantAsync(
        ICrossTenantRebuildSource crossTenantSource,
        Recorder recorder,
        CancellationToken cancellation)
    {
        var progress = await Database.AllProjectionProgress(cancellation).ConfigureAwait(false);

        foreach (var asyncShard in _store.AllShards())
        {
            if (cancellation.IsCancellationRequested) return;

            var tenants = await crossTenantSource
                .FindRebuildTenantsAsync(asyncShard.Name.Name, cancellation)
                .ConfigureAwait(false);

            if (tenants.Count == 0)
            {
                // No registered tenants for this projection — nothing to catch up.
                continue;
            }

            foreach (var tenantId in tenants)
            {
                _tenantHighWater!.PolledTenants.Activate(tenantId);
            }
            await pollTenantHighWaterAsync().ConfigureAwait(false);

            foreach (var tenantId in tenants)
            {
                if (cancellation.IsCancellationRequested) return;

                var ceiling = _tenantHighWater!.CeilingFor(tenantId) ?? 0L;
                if (ceiling == 0L)
                {
                    // Tenant exists but has no events for this projection yet.
                    continue;
                }

                var tenantShard = asyncShard with { Name = asyncShard.Name.ForTenant(tenantId) };
                var state = progress.FirstOrDefault(x => x.ShardName == tenantShard.Name.Identity)
                            ?? new ShardState(tenantShard.Name, 0);
                var agent = buildAgentForShard(tenantShard);

                await agent.CatchUpAsync(ceiling, state, cancellation).ConfigureAwait(false);
                throwIfRecordedExceptions(recorder, cancellation);
            }
        }
    }

    private void throwIfRecordedExceptions(Recorder recorder, CancellationToken cancellation)
    {
        var exceptions = recorder.States
            .Select(x => x.Exception)
            .Where(x => x != null)
            .Where(x => cancellation.IsCancellationRequested || !isCancellationNoise(x!))
            .ToArray();
        if (exceptions.Length != 0)
        {
            throw new AggregateException(exceptions!);
        }
    }

    public async Task CatchUpAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeout);
        await CatchUpAsync(cts.Token);
    }

    private static bool isCancellationNoise(Exception exception)
    {
        if (exception is OperationCanceledException) return true;
        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Count > 0
                   && aggregate.InnerExceptions.All(isCancellationNoise);
        }

        return false;
    }
}

internal class Recorder : IObserver<ShardState>
{
    public ConcurrentBag<ShardState> States { get; } = new();
    
    public void OnCompleted()
    {
        
    }

    public void OnError(Exception error)
    {
        
    }

    public void OnNext(ShardState value)
    {
        States.Add(value);
    }
}

