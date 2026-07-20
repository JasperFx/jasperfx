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
using Timer = System.Timers.Timer;

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

    // wolverine#3519 / jasperfx#534: the last exception a start attempt caught, keyed by shard identity.
    // tryStartAgentAsync swallows a faulted start into a bool; stashing the cause here lets
    // StartAgentAsync(ShardName) attach it as the inner exception instead of throwing a causeless one.
    private ImHashMap<string, Exception> _lastStartFailures = ImHashMap<string, Exception>.Empty;
    private CancellationTokenSource _cancellation = new();
    private readonly HighWaterAgent _highWater;
    private readonly IDisposable _breakSubscription;

    // jasperfx#537: persists agent status transitions + heartbeat ticks onto the store's extended
    // progression columns when the store opts in via IEventStore.ExtendedProgressionEnabled
    private readonly ExtendedProgressionWriter _extendedProgression;
    private readonly IDisposable _extendedProgressionSubscription;
    private RetryBlock<DeadLetterEvent> _deadLetterBlock;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    // Only non-null when the backing store partitions events per tenant; null keeps the daemon on the
    // single store-global high-water mark (today's behavior, byte for byte). jasperfx#407 Phase 2b.
    private readonly TenantedHighWaterCoordinator? _tenantHighWater;

    // jasperfx#492: OnNext(HighWaterMark) only fires when the STORE-GLOBAL mark changes, so a lagging
    // tenant appending below the global max would never be re-polled in a quiet system. This timer
    // guarantees a per-tenant poll at least every SlowPollingTime; _lastTenantHighWaterPoll dedups it
    // against the OnNext-driven fast path so an active system still polls once per global tick.
    private readonly Timer? _tenantHighWaterTimer;
    private DateTimeOffset _lastTenantHighWaterPoll;
    private int _tenantHighWaterPollInFlight;

    // jasperfx#494 (epic #486 WS2): shared by every agent loader this daemon builds so the
    // database's connection footprint stays O(databases), not O(agents). Null = unthrottled.
    private SemaphoreSlim? _loadThrottle;
    private int _maxConcurrentEventLoads;

    // Epic #486 WS3: bounds concurrent projection batch execute/commit sessions. Reaches the
    // executions through IDaemonRuntime -> SubscriptionAgent -> EventRange.Agent. Null = unbounded.
    private SemaphoreSlim? _batchWriteThrottle;
    private int _maxConcurrentBatchWrites;

    public SemaphoreSlim? BatchWriteThrottle => _batchWriteThrottle;

    /// <summary>
    /// The per-database cap on concurrent event loads. Setting it replaces the throttle for agent
    /// loaders built AFTER the change — a running agent captured its loader's throttle when it was
    /// built (see ThrottledEventLoader), so resizing does not retroactively narrow an in-flight load.
    /// Null or a non-positive value is unthrottled. Mirrors <see cref="MaxConcurrentRebuildsPerDatabase"/>:
    /// the previous semaphore is deliberately not disposed, since callers may still be waiting on it.
    /// </summary>
    public int MaxConcurrentEventLoadsPerDatabase
    {
        get => _maxConcurrentEventLoads;
        set
        {
            _maxConcurrentEventLoads = value;
            _loadThrottle = value > 0 ? new SemaphoreSlim(value) : null;
        }
    }

    /// <summary>
    /// The per-database cap on concurrent projection batch execute/commit sessions. Unlike
    /// <see cref="MaxConcurrentEventLoadsPerDatabase"/> this one reaches running agents immediately,
    /// because they read it through a live pass-through (SubscriptionAgent.BatchWriteThrottle) rather
    /// than capturing it. Null or a non-positive value is unbounded. The previous semaphore is
    /// deliberately not disposed, since callers may still be waiting on it.
    /// </summary>
    public int MaxConcurrentBatchWritesPerDatabase
    {
        get => _maxConcurrentBatchWrites;
        set
        {
            _maxConcurrentBatchWrites = value;
            _batchWriteThrottle = value > 0 ? new SemaphoreSlim(value) : null;
        }
    }

    // jasperfx#497 (the #420 leftover): ONE shared budget per daemon (= per database) for rebuild
    // cells, spanning both fan-out layers — the CLI's projection-level fan-out AND the
    // intra-projection per-(tenant, shard) fan-out — so a projection-level slot and its tenant
    // cells never multiply the bound. Each cell holds a slot only for the duration of its replay
    // (rebuildAgent). Null = unbounded.
    private SemaphoreSlim? _rebuildBudget;
    private int? _maxConcurrentRebuilds;

    /// <summary>
    /// jasperfx#497: the shared per-database rebuild cell budget. Resolved at construction from
    /// <see cref="DaemonSettings.MaxConcurrentRebuildsPerDatabase"/> (explicit knob) falling back to
    /// <see cref="IEventStore.MaxConcurrentRebuildsPerDatabase"/> (store-derived default, e.g.
    /// Marten/Polecat's pool-size / 8). Setting it — the <c>projections rebuild --max-concurrent</c>
    /// CLI override path — replaces the budget for subsequent rebuild operations. Null or a
    /// non-positive value is unbounded.
    /// </summary>
    public int? MaxConcurrentRebuildsPerDatabase
    {
        get => _maxConcurrentRebuilds;
        set
        {
            _maxConcurrentRebuilds = value;
            _rebuildBudget = value is > 0 ? new SemaphoreSlim(value.Value) : null;
        }
    }

    public JasperFxAsyncDaemon(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory, IHighWaterDetector detector, ProjectionGraph<TProjection, TOperations, TQuerySession> projections)
    {
        Database = database;
        _store = store;
        _loggerFactory = loggerFactory;
        _projections = projections;
        Logger = loggerFactory.CreateLogger(GetType());
        Tracker = Database.Tracker;
        // A multi-database store runs one of these per database, all publishing the same shard names.
        // Stamping the tracker is what lets a consumer tell them apart. See CritterWatch#678.
        Tracker.DatabaseIdentifier ??= Database.Identifier;
        _highWater = new HighWaterAgent(store.Meter, detector, Tracker, loggerFactory.CreateLogger<HighWaterAgent>(), projections, _cancellation.Token);

        if (detector.SupportsTenantPartitioning)
        {
            _tenantHighWater = new TenantedHighWaterCoordinator(detector, loggerFactory.CreateLogger<TenantedHighWaterCoordinator>());
            _tenantHighWaterTimer = buildTenantHighWaterTimer();
        }

        _breakSubscription = database.Tracker.Subscribe(this);

        // jasperfx#537: subscribe unconditionally; the writer checks the store's
        // ExtendedProgressionEnabled flag live per publication so runtime opt-in is honored
        _extendedProgression = new ExtendedProgressionWriter(store, database, store.TimeProvider,
            loggerFactory.CreateLogger<ExtendedProgressionWriter>());
        _extendedProgressionSubscription = Tracker.Subscribe(_extendedProgression);

        _deadLetterBlock = buildDeadLetterBlock();

        MaxConcurrentEventLoadsPerDatabase = _projections.MaxConcurrentEventLoadsPerDatabase;
        MaxConcurrentBatchWritesPerDatabase = _projections.MaxConcurrentBatchWritesPerDatabase;

        // jasperfx#497: explicit DaemonSettings knob wins, then the store-derived default. Concrete
        // stores typically fold the settings knob into their override already; the double-consult is
        // idempotent. Null resolves to null = unbounded (JasperFx.Events has no pool signal).
        MaxConcurrentRebuildsPerDatabase =
            _projections.MaxConcurrentRebuildsPerDatabase ?? store.MaxConcurrentRebuildsPerDatabase;
    }

    public JasperFxAsyncDaemon(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger, IHighWaterDetector detector, ProjectionGraph<TProjection, TOperations, TQuerySession> projections)
    {
        Database = database;
        _store = store;
        _projections = projections;
        _loggerFactory = null;
        Logger = logger;
        Tracker = Database.Tracker;
        Tracker.DatabaseIdentifier ??= Database.Identifier;
        _highWater = new HighWaterAgent(store.Meter, detector, Tracker, logger, _projections, _cancellation.Token);

        if (detector.SupportsTenantPartitioning)
        {
            _tenantHighWater = new TenantedHighWaterCoordinator(detector, logger);
            _tenantHighWaterTimer = buildTenantHighWaterTimer();
        }

        _breakSubscription = database.Tracker.Subscribe(this);

        // jasperfx#537: see the ILoggerFactory constructor overload for the rationale
        _extendedProgression = new ExtendedProgressionWriter(store, database, store.TimeProvider, logger);
        _extendedProgressionSubscription = Tracker.Subscribe(_extendedProgression);

        _deadLetterBlock = buildDeadLetterBlock();

        MaxConcurrentEventLoadsPerDatabase = _projections.MaxConcurrentEventLoadsPerDatabase;
        MaxConcurrentBatchWritesPerDatabase = _projections.MaxConcurrentBatchWritesPerDatabase;

        // jasperfx#497: see the ILoggerFactory constructor overload for the resolution rationale
        MaxConcurrentRebuildsPerDatabase =
            _projections.MaxConcurrentRebuildsPerDatabase ?? store.MaxConcurrentRebuildsPerDatabase;
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
        _tenantHighWaterTimer?.Stop();
        _tenantHighWaterTimer?.Dispose();
        _breakSubscription.Dispose();
        _extendedProgressionSubscription.Dispose();
        // Completes the writer's queue so a final Stopped write can drain in the background
        _ = _extendedProgression.DisposeAsync();
        _deadLetterBlock.Dispose();
        _loadThrottle?.Dispose();
        _batchWriteThrottle?.Dispose();
        _rebuildBudget?.Dispose();
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
            // jasperfx#534: this false path was silent. It is benign (the agent is already running, so a
            // TryFind by the caller succeeds), but logging it at Debug keeps the "why did my start return
            // false" trail unbroken.
            Logger.LogDebug("Start of agent {ShardName} skipped: an agent is already running for this shard",
                agent.Name.Identity);
            return false;
        }

        // Lock
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            // Be idempotent, don't start an agent that is already running now that we have the lock.
            if (_agents.TryFind(agent.Name.Identity, out running) && running.Status == AgentStatus.Running)
            {
                Logger.LogDebug("Start of agent {ShardName} skipped: an agent is already running for this shard",
                    agent.Name.Identity);
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

            // jasperfx#534: a prior failed start for this identity has now been superseded by a success.
            _lastStartFailures = _lastStartFailures.Remove(agent.Name.Identity);
            syncTenantPolling();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error trying to start agent {ShardName}", agent.Name.Identity);

            // jasperfx#534: stash the cause so StartAgentAsync(ShardName) can attach it to the exception it
            // throws instead of a causeless "Unable to start" that fills a caller's retry log forever.
            _lastStartFailures = _lastStartFailures.AddOrUpdate(agent.Name.Identity, ex);

            // jasperfx#540: agent.StartAsync may have already spun up the execution pipeline and heartbeat
            // and begun advancing before it faulted. This agent was NEVER added to _agents, so no later
            // StopAgentAsync can reach it -- returning now would leave it orphaned, still holding the
            // shard's execution loop. On multi-store / Wolverine-managed hosts that is a candidate for the
            // permanent first-start wedge in wolverine#3519. Hard-stop it here so a faulted start is always
            // fully released at the point of failure, independent of what the caller does. Guarded so a
            // secondary teardown failure never masks the original cause.
            try
            {
                await agent.HardStopAsync().ConfigureAwait(false);
            }
            catch (Exception teardownEx)
            {
                Logger.LogDebug(teardownEx,
                    "Error tearing down partially-started agent {ShardName} after a failed start",
                    agent.Name.Identity);
            }

            return false;
        }
        finally
        {
            _semaphore.Release();
        }

        return true;
    }

    // jasperfx#497: one rebuild "cell" — a single (projection, tenant/shard) replay. The cell draws a
    // slot from the shared per-database rebuild budget for the duration of its replay, so no matter how
    // wide the caller's fan-out is (the CLI's projection-level Parallel.ForEachAsync, the per-tenant
    // cross-product loop, CrossTenantRebuild.RebuildEverywhereAsync), the number of concurrently
    // replaying cells per database never exceeds the budget. The daemon's agent-registry lock
    // (_semaphore) is now held only around the registry mutations, NOT across the replay itself —
    // holding it across the replay (the pre-#497 shape) serialized every rebuild cell in the daemon at
    // an effective concurrency of one, making any cap > 1 unreachable.
    // The optional floor/disableOptimizedReplay parameters exist for the jasperfx#480 side-effect
    // version gate: its bounded replay resumes from the new version's own persisted progress (not 0)
    // and must not route through a store replay executor that ignores the custom ceiling.
    private async Task rebuildAgent(ISubscriptionAgent agent, long highWaterMark, TimeSpan shardTimeout,
        long floor = 0, bool disableOptimizedReplay = false)
    {
        var budget = _rebuildBudget;
        if (budget != null)
        {
            await budget.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        }

        try
        {
            await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

            try
            {
                // Ensure that the agent is stopped if it is already running
                await stopIfRunningAsync(agent.Name.Identity).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }

            var errorOptions = _store.RebuildErrors;

            var request = new SubscriptionExecutionRequest(floor, ShardExecutionMode.Rebuild, errorOptions, this)
            {
                DisableOptimizedReplay = disableOptimizedReplay
            };
            await agent.ReplayAsync(request, highWaterMark, shardTimeout).ConfigureAwait(false);

            await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

            try
            {
                _agents = _agents.AddOrUpdate(agent.Name.Identity, agent);
            }
            finally
            {
                _semaphore.Release();
            }
        }
        finally
        {
            budget?.Release();
        }
    }

    // jasperfx#480: the bounded warm-up replay is capped like any other rebuild; a timeout leaves the
    // shard stopped with its partial progress persisted, and the next start resumes the warm-up from
    // that floor (the gate triggers on progress < prior mark, not only on zero progress).
    private static readonly TimeSpan SideEffectGateTimeout = 5.Minutes();

    // jasperfx#480: single entry point for starting a shard in Continuous mode so every start path
    // (StartAllAsync, StartAgentAsync by name, the per-tenant fan-outs) runs the opt-in blue/green
    // side-effect gate first. Returns true when the continuous agent was actually started.
    private async Task<bool> startContinuousShardAsync(AsyncShard<TOperations, TQuerySession> shard)
    {
        if (!await tryApplySideEffectVersionGateAsync(shard).ConfigureAwait(false))
        {
            // The warm-up replay failed; leave the shard stopped rather than starting continuous
            // execution that would emit side effects over history the prior version already covered.
            return false;
        }

        var agent = buildAgentForShard(shard);
        var started = await tryStartAgentAsync(agent, ShardExecutionMode.Continuous).ConfigureAwait(false);

        if (!started && agent is IAsyncDisposable d)
        {
            await d.DisposeAsync().ConfigureAwait(false);
        }

        return started;
    }

    // jasperfx#480: opt-in blue/green side-effect gate. When a projection opts in and a NEW version of
    // it starts behind the highest PRIOR version's persisted progression mark N, first run a bounded
    // replay to N in Rebuild mode (side effects suppressed, aggregate state correct), then let the
    // caller hand off to Continuous — DetermineStartingPositionAsync reads the persisted progress (now
    // N) so side effects only fire for events the previous version never processed. Returns false ONLY
    // when the warm-up replay was attempted and failed; every not-triggered case returns true.
    private async Task<bool> tryApplySideEffectVersionGateAsync(AsyncShard<TOperations, TQuerySession> shard)
    {
        var name = shard.Name;
        if (!shard.Options.GateSideEffectsBehindPriorVersion || name.Version <= 1)
        {
            return true;
        }

        if (shard.Options.UsesFromPresent(Database))
        {
            Logger.LogWarning(
                "Projection shard {Name} opts into GateSideEffectsBehindPriorVersion but subscribes from 'present', which ignores persisted progression. The side-effect gate is skipped",
                name.Identity);
            return true;
        }

        try
        {
            var current = await Database.ProjectionProgressFor(name, _cancellation.Token).ConfigureAwait(false);
            var prior = await resolvePriorVersionProgressAsync(name, _cancellation.Token).ConfigureAwait(false);

            // Triggering on current < prior (rather than only current == 0) makes an interrupted
            // warm-up resumable: a crash mid-replay leaves progress at M < N, and the next start
            // suppresses side effects for the remaining (M, N] instead of re-emitting them.
            if (prior <= current)
            {
                return true;
            }

            Logger.LogInformation(
                "Projection shard {Name} v{Version} is behind the prior version's progression ({Current} < {Prior}); replaying to {Prior} with side effects suppressed before continuous execution starts (blue/green side-effect gate)",
                name.Identity, name.Version, current, prior, prior);

            var warmup = buildAgentForShard(shard);
            Tracker.MarkAsRestarted(warmup.Name);

            try
            {
                await rebuildAgent(warmup, prior, SideEffectGateTimeout, floor: current, disableOptimizedReplay: true)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not TimeoutException)
            {
                // A failed replay faults ReplayAsync: the agent pauses itself via
                // ReportCriticalFailureAsync and faults the rebuild completion, which rebuildAgent
                // propagates — skipping its registration step. Register the paused agent here so the
                // shard is observably Paused and carries the failure for observers, and do not start
                // continuous execution over history the prior version already covered. A TimeoutException
                // is NOT an agent failure and falls through to the outer catch: the wedged agent is
                // disposed but never paused, so registering it would misreport the shard as Running.
                Logger.LogError(e,
                    "The blue/green side-effect gate warm-up for projection shard {Name} failed at {Position}. The shard is left paused; restarting it will resume the suppressed warm-up from its persisted progress",
                    name.Identity, warmup.LastCommitted);

                await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                try
                {
                    _agents = _agents.AddOrUpdate(warmup.Name.Identity, warmup);
                }
                finally
                {
                    _semaphore.Release();
                }

                return false;
            }

            // Belt and braces: a replay that returned normally should have left the agent Running at
            // the target mark, but judge the warm-up by the agent's own state anyway before enabling
            // side effects. On failure, leave the agent registered and do not start continuous execution.
            if (warmup.Status != AgentStatus.Running || warmup.LastCommitted < prior)
            {
                Logger.LogError(
                    "The blue/green side-effect gate warm-up for projection shard {Name} stopped at {Position} in status {Status} before reaching {Prior}. The shard is left in that state; restarting it will resume the suppressed warm-up from its persisted progress",
                    name.Identity, warmup.LastCommitted, warmup.Status, prior);
                return false;
            }

            using var cancellation = new CancellationTokenSource(5.Seconds());
            try
            {
                // Marks the warm-up agent Stopped so the continuous start below is not mistaken
                // for a duplicate of a running agent (mirrors the rebuildProjection sequence).
                await warmup.StopAndDrainAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop and drain the side-effect gate warm-up agent {Name}",
                    warmup.Name.Identity);
            }

            Logger.LogInformation(
                "Projection shard {Name} v{Version} finished its side-effect-suppressed warm-up at {Prior}; side effects are enabled from here on",
                name.Identity, name.Version, prior);

            return true;
        }
        catch (Exception e)
        {
            Logger.LogError(e,
                "Error running the blue/green side-effect gate for projection shard {Name}. The shard is left stopped; restarting it will resume the suppressed warm-up from its persisted progress",
                name.Identity);
            return false;
        }
    }

    // jasperfx#480: the one genuinely new read — resolve the HIGHEST prior version's persisted
    // progression mark for the same (projection, shard key, tenant). The version is baked into the
    // progression-row identity (Trips:V2:All vs Trips:V3:All are distinct rows), so the prior mark
    // survives the version bump and parses back out of AllProjectionProgress here.
    private async Task<long> resolvePriorVersionProgressAsync(ShardName name, CancellationToken token)
    {
        var progress = await Database.AllProjectionProgress(token).ConfigureAwait(false);

        long mark = 0;
        uint priorVersion = 0;
        foreach (var state in progress)
        {
            if (!ShardName.TryParse(state.ShardName, out var parsed) || parsed == null)
            {
                continue;
            }

            if (parsed.Version >= name.Version || parsed.Version < priorVersion)
            {
                continue;
            }

            if (!parsed.Name.EqualsIgnoreCase(name.Name)) continue;
            if (!parsed.ShardKey.EqualsIgnoreCase(name.ShardKey)) continue;
            if (!string.Equals(parsed.TenantId, name.TenantId)) continue;

            priorVersion = parsed.Version;
            mark = state.Sequence;
        }

        return mark;
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


        // Exact registered identities always win — a shard identity that happens to contain enough
        // segments to parse as tenant-bearing must not be hijacked by the per-tenant branch below.
        var shard = _store.AllShards().FirstOrDefault(x => x.Name.Identity == shardName);
        if (shard != null)
        {
            await startContinuousShardAsync(shard).ConfigureAwait(false);
            return;
        }

        // wolverine#3280: a per-tenant identity ("<proj>:All:<tenant>", or versioned
        // "<proj>:V{n}:All:<tenant>") is requested individually under node-distributed daemons
        // (Wolverine-managed distribution). AllShards() only carries the store-global identities, so
        // resolve the BASE shard and fan out a per-tenant agent — the same shape
        // buildPerTenantContinuousAgents uses — activating the tenant in the high-water coordinator so it
        // advances against its own mark and persists its own <proj>:All:<tenant> progression row.
        if (ShardName.TryParse(shardName, out var requested) && requested?.TenantId != null)
        {
            if (_tenantHighWater == null)
            {
                // Without per-tenant high-water tracking a tenant agent would seed from the store-global
                // mark and double-process events already covered by the store-global agent. A tenant-bearing
                // identity arriving here means the host (e.g. Wolverine) fanned out per-tenant agents
                // against a store that does not distribute per tenant — fail loudly instead.
                throw new ArgumentOutOfRangeException(nameof(shardName),
                    $"Shard name '{shardName}' addresses tenant '{requested.TenantId}', but this event store does not use per-tenant event partitioning. Value options are {_store.AllShards().Select(x => x.Name.Identity).Join(", ")}");
            }

            var baseIdentity = ShardName.Compose(requested.Name, requested.ShardKey, null, requested.Version).Identity;
            var baseShard = _store.AllShards().FirstOrDefault(x => x.Name.Identity == baseIdentity);
            if (baseShard == null)
            {
                throw new ArgumentOutOfRangeException(nameof(shardName),
                    $"Unknown shard name '{shardName}'. Value options are {_store.AllShards().Select(x => x.Name.Identity).Join(", ")}");
            }

            // Prime the tenant's ceiling BEFORE starting the agent, mirroring StartAllAsync's
            // prime-then-start order. tryStartAgentAsync seeds the agent from CeilingFor(tenant) and the
            // starting position strategy runs against that ceiling — starting first and polling after
            // would run DetermineStartingPositionAsync against high-water 0, which for a
            // SubscribeFromPresent subscription resolves "present" to sequence 0 and rewinds its
            // progression row, replaying the tenant's entire history.
            _tenantHighWater.PolledTenants.Activate(requested.TenantId);
            await pollTenantHighWaterAsync().ConfigureAwait(false);

            var tenantShard = baseShard with { Name = baseShard.Name.ForTenant(requested.TenantId) };
            var tenantStarted = await startContinuousShardAsync(tenantShard).ConfigureAwait(false);
            if (!tenantStarted)
            {
                // Reconcile the polled-tenant set so a failed start doesn't leave the coordinator
                // polling (and persisting high-water rows for) a tenant with no agent on this node.
                syncTenantPolling();
            }

            return;
        }

        throw new ArgumentOutOfRangeException(nameof(shardName),
            $"Unknown shard name '{shardName}'. Value options are {_store.AllShards().Select(x => x.Name.Identity).Join(", ")}");
    }
    
    public async Task<ISubscriptionAgent> StartAgentAsync(ShardName name, CancellationToken token)
    {
        await StartAgentAsync(name.Identity, token);
        if (_agents.TryFind(name.Identity, out var agent)) return agent;

        // wolverine#3519 / jasperfx#534: the string-overload start returned without throwing, yet nothing
        // is registered under this identity. Callers (e.g. Wolverine's EventSubscriptionAgent) previously
        // got a bare Exception with no context and wedged in a permanent retry loop with no way to see why.
        // If a start attempt actually faulted, attach that cause; otherwise surface the daemon state that
        // explains the miss instead of masking it.
        if (_lastStartFailures.TryFind(name.Identity, out var cause))
        {
            throw new ShardStartException(name.Identity, cause);
        }

        throw new ShardStartException(name.Identity, describeStartFailure(name));
    }

    // wolverine#3519: turn the "agent not registered after a start that did not throw" miss into an
    // actionable message. The usual causes are a startup race on multi-store / Wolverine-managed hosts
    // (high-water detection still coming up, or a concurrent stop/replace evicting the just-registered
    // agent) and a blue/green side-effect gate leaving the shard paused rather than continuous.
    private string describeStartFailure(ShardName name)
    {
        if (_agents.TryFind(name.Identity, out var existing))
        {
            return $"An agent is registered for this shard in status '{existing.Status}' rather than running. It may have been paused by an error or a blue/green side-effect gate warm-up; check the log for the pause reason and restart the shard once resolved.";
        }

        if (!_highWater.IsRunning)
        {
            return "High-water detection is not running yet, so the shard could not be positioned. This is typically a transient startup race; retrying the start once high-water detection is up should succeed.";
        }

        var known = _store.AllShards().Select(x => x.Name.Identity).ToArray();
        if (!known.Contains(name.Identity, StringComparer.OrdinalIgnoreCase))
        {
            return $"No such shard is registered with this store. Known shards are: {known.Join(", ")}.";
        }

        return "The shard is registered but did not start and did not report an error, which points at a startup race between concurrent agent starts on this daemon. Retrying the start usually succeeds.";
    }

    public Task StopAgentAsync(ShardName shardName, Exception? ex = null)
    {
        return StopAgentAsync(shardName.Identity);
    }

    private SubscriptionAgent buildAgentForShard(AsyncShard<TOperations, TQuerySession> shard)
    {
        var execution = _loggerFactory == null ? shard.Factory.BuildExecution(_store, Database, Logger, shard.Name) : shard.Factory.BuildExecution(_store, Database, _loggerFactory, shard.Name);
        var loader = _store.BuildEventLoader(Database, Logger, shard.Filters, shard.Options, shard.Name);

        if (_loadThrottle != null)
        {
            // jasperfx#494: all of this daemon's agents share one load throttle so the pool's
            // high-water mark stays bounded no matter how many (projection × tenant) agents run
            loader = new ThrottledEventLoader(loader, _loadThrottle);
        }

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

        var shards = new List<AsyncShard<TOperations, TQuerySession>>();

        if (_tenantHighWater != null && Database is ICrossTenantRebuildSource crossTenantSource)
        {
            // marten#4717: under per-tenant event partitioning each tenant's events draw seq_id from its
            // own mt_events_sequence_{suffix} starting at 1, so a single store-global <Projection>:All
            // shard cannot track multiple tenants. Fan out one continuous agent per (shard, tenant) —
            // exactly the shape catchUpPerTenantAsync / rebuildProjectionForTenant already use — so each
            // tenant's projection advances against its own high-water and persists its own
            // <Projection>:All:<tenant> progression row. OnNext + pollTenantHighWaterAsync already route
            // each tenant's mark to its TenantId-bearing agents.
            await buildPerTenantContinuousShards(crossTenantSource, shards).ConfigureAwait(false);

            // Prime the per-tenant ceilings BEFORE starting the agents so each tenant agent seeds from
            // its own high-water (tryStartAgentAsync reads CeilingFor) rather than the store-global mark.
            // PollAsync populates the monitor's ceilings directly from pg_sequences, independent of the
            // store-global high-water agent, so the readings are available even pre-start.
            await pollTenantHighWaterAsync().ConfigureAwait(false);
        }
        else
        {
            shards.AddRange(_store.AllShards());
        }

        foreach (var shard in shards)
        {
            await startContinuousShardAsync(shard).ConfigureAwait(false);
        }
    }

    // marten#4717: build one continuous shard per (shard, tenant), enumerating tenants from the store's
    // ICrossTenantRebuildSource (mt_tenant_partitions). A projection with no registered tenants yet keeps
    // its store-global shard so it still runs (there are no events to process until a tenant exists).
    private async Task buildPerTenantContinuousShards(
        ICrossTenantRebuildSource crossTenantSource, List<AsyncShard<TOperations, TQuerySession>> shards)
    {
        foreach (var shard in _store.AllShards())
        {
            var tenants = await crossTenantSource
                .FindRebuildTenantsAsync(shard.Name.Name, _cancellation.Token).ConfigureAwait(false);

            if (tenants.Count == 0)
            {
                shards.Add(shard);
                continue;
            }

            foreach (var tenantId in tenants)
            {
                _tenantHighWater!.PolledTenants.Activate(tenantId);
                shards.Add(shard with { Name = shard.Name.ForTenant(tenantId) });
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
        // PS#5 addendum — stamp the daemon's StoreUri so observers that
        // subscribed directly via `daemon.Tracker.Subscribe(observer)`,
        // bypassing the `SubscribeWithStoreUriStamp` extension, still
        // attribute the state to the owning store.
        //
        // Mechanics: the daemon subscribes itself to its Tracker in the
        // constructor (`database.Tracker.Subscribe(this)`). Every published
        // `ShardState` instance is broadcast to all listeners in subscription
        // order, sharing the same object. Mutating `value.StoreUri` here
        // means every subsequent listener in the broadcast loop sees the
        // stamped value. The helper preserves any upstream-set value so a
        // chained `StoreUriStampingObserver` (the extension path) wins over
        // this daemon-level default and so multi-daemon scenarios attached
        // to a shared per-database Tracker don't fight.
        StoreUriStampingObserver.StampIfMissing(value, StoreUri);

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

        _lastTenantHighWaterPoll = DateTimeOffset.UtcNow;

        try
        {
            await _tenantHighWater.PollAndRouteAsync(CurrentAgents(), _cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Error polling per-tenant high water for database {Name}", Database.Identifier);
        }
    }

    // jasperfx#492: guarantee a per-tenant high-water poll on a reliable cadence, not solely on
    // store-global mark publications. Runs for the daemon's lifetime; each tick is a no-op unless the
    // high-water agent is running and no poll happened within the last SlowPollingTime window.
    private Timer buildTenantHighWaterTimer()
    {
        var timer = new Timer(_projections.SlowPollingTime.TotalMilliseconds) { AutoReset = true };
        timer.Elapsed += (_, _) =>
        {
            syncTenantHighWaterInterval();
            _ = pollTenantHighWaterOnCadenceAsync();
        };
        timer.Start();
        return timer;
    }

    /// <summary>
    /// Re-read SlowPollingTime every tick so a caller that re-paces the daemon at runtime is honored.
    /// The interval used to be captured at construction, which left this timer polling at the original
    /// cadence for the life of the daemon no matter what SlowPollingTime was changed to — the high-water
    /// loop itself re-reads its polling times every wait, so this was the odd one out.
    /// </summary>
    private void syncTenantHighWaterInterval()
    {
        if (_tenantHighWaterTimer == null) return;

        var desired = _projections.SlowPollingTime.TotalMilliseconds;
        if (desired <= 0) return;

        // Assigning Interval restarts the timer, so only touch it on a real change
        if (Math.Abs(_tenantHighWaterTimer.Interval - desired) > 1)
        {
            _tenantHighWaterTimer.Interval = desired;
        }
    }

    private async Task pollTenantHighWaterOnCadenceAsync()
    {
        if (!_highWater.IsRunning || _cancellation.IsCancellationRequested)
        {
            return;
        }

        // The OnNext(HighWaterMark) fast path already polled within this cadence window
        if (DateTimeOffset.UtcNow - _lastTenantHighWaterPoll < _projections.SlowPollingTime)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _tenantHighWaterPollInFlight, 1, 0) == 1)
        {
            return;
        }

        try
        {
            await pollTenantHighWaterAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _tenantHighWaterPollInFlight, 0);
        }
    }

    // Keep the vectorized monitor's polled-tenant set in step with the shards currently assigned to this
    // node. No-op for non-partitioned stores. jasperfx#407 Phase 2b.
    private void syncTenantPolling()
    {
        var assigned = CurrentAgents().Select(x => x.Name).ToArray();
        _tenantHighWater?.SyncAssignedTenants(assigned);

        // Idle the cadence timer along with the daemon's actual workload. It used to run from
        // construction until Dispose(), so a daemon that had been stopped — or one whose database was
        // handed to another node — kept waking every SlowPollingTime forever to do nothing. The poll
        // itself already no-ops via the _highWater.IsRunning guard, so this is housekeeping rather
        // than a correctness fix, and StartAgentAsync's syncTenantPolling call starts it again.
        if (_tenantHighWaterTimer == null) return;

        if (assigned.Length == 0)
        {
            _tenantHighWaterTimer.Stop();
        }
        else if (!_tenantHighWaterTimer.Enabled)
        {
            syncTenantHighWaterInterval();
            _tenantHighWaterTimer.Start();
        }
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
        if (tenantId != null)
        {
            if (_projections.TryFindProjection(projectionName, out var perTenantSource))
            {
                return rebuildProjectionForTenant(perTenantSource, tenantId, shardTimeout, token);
            }

            throw new ArgumentOutOfRangeException(nameof(projectionName),
                $"No registered projection matches the name '{projectionName}'. Available names are {_projections.AllProjectionNames().Join(", ")}");
        }

        // CritterWatch#303 / #371: store-global rebuild (null tenant). Under per-tenant event
        // partitioning the store-global mt_events_sequence is stale, so the plain store-global rebuild
        // gates on Tracker.HighWaterMark==0 and aborts — it would visit NO tenant's shard. Fan out and
        // rebuild every registered tenant's shard instead, exactly as CatchUpAsync does. Non-partitioned
        // stores (no ICrossTenantRebuildSource / no tenant high-water) fall through to the unchanged
        // store-global rebuild, so single-tenant behavior is byte-for-byte.
        if (_tenantHighWater != null && Database is ICrossTenantRebuildSource crossTenantSource
            && _projections.TryFindProjection(projectionName, out var source))
        {
            return rebuildProjectionAllTenants(crossTenantSource, source, shardTimeout, token);
        }

        return RebuildProjectionAsync(projectionName, shardTimeout, token);
    }

    // CritterWatch#303 / #371: visit EVERY registered tenant's shard for a store-global rebuild under
    // partitioning, reusing the per-tenant rebuild that scopes teardown + ceiling to (shard, tenant).
    // Tenant enumeration mirrors catchUpPerTenantAsync. With no tenants registered yet, fall back to the
    // store-global rebuild (a no-op when the high-water is 0).
    //
    // jasperfx#497: when a rebuild budget is configured, the per-tenant fan-out runs in parallel with a
    // launch width of the budget size — the actual replay concurrency is bounded by the SHARED
    // per-database budget inside rebuildAgent, so overlapping projection-level rebuilds (the CLI's
    // --max-concurrent layer) and their tenant cells never multiply the bound. With no budget
    // (null/non-positive = unbounded core-side), the historical sequential tenant walk is preserved.
    private async Task rebuildProjectionAllTenants(
        ICrossTenantRebuildSource crossTenantSource,
        IProjectionSource<TOperations, TQuerySession> source,
        TimeSpan shardTimeout,
        CancellationToken token)
    {
        var tenants = await crossTenantSource
            .FindRebuildTenantsAsync(source.Name, token).ConfigureAwait(false);

        if (tenants.Count == 0)
        {
            await RebuildProjectionAsync(source.Name, shardTimeout, token).ConfigureAwait(false);
            return;
        }

        // ParallelOptions.MaxDegreeOfParallelism throws on 0 — only a positive budget may pass through.
        var maxConcurrent = MaxConcurrentRebuildsPerDatabase;
        if (maxConcurrent is > 0)
        {
            await Parallel.ForEachAsync(tenants,
                    new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = maxConcurrent.Value },
                    async (tenantId, ct) =>
                        await rebuildProjectionForTenant(source, tenantId, shardTimeout, ct).ConfigureAwait(false))
                .ConfigureAwait(false);

            return;
        }

        foreach (var tenantId in tenants)
        {
            if (token.IsCancellationRequested) return;
            await rebuildProjectionForTenant(source, tenantId, shardTimeout, token).ConfigureAwait(false);
        }
    }

    // CritterWatch#303: per-tenant (or store-global) shard pause. A null tenant stops every shard of the
    // projection; a non-null tenant stops only that tenant's shard(s). The running agents are matched by
    // (projection name [, tenant]) — exactly the filter the per-tenant rebuild uses — so the caller never
    // has to reconstruct the shard identity/version. Each match is routed through StopAgentAsync, which
    // stops, drains, and REMOVES the agent from the running set (so CurrentAgents()/StatusFor reflect the
    // pause immediately), unlike the rebuild-internal hard-stop that leaves the agent in place for the
    // rebuild to replace. Resume via StartAllAsync.
    public async Task PauseShardAsync(string projectionName, string? tenantId, CancellationToken token)
    {
        if (!_projections.TryFindProjection(projectionName, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(projectionName),
                $"No registered projection matches the name '{projectionName}'. Available names are {_projections.AllProjectionNames().Join(", ")}");
        }

        var targets = CurrentAgents()
            .Where(x => x.Name.Name == projectionName && (tenantId == null || x.Name.TenantId == tenantId))
            .Select(x => x.Name.Identity)
            .ToArray();

        foreach (var identity in targets)
        {
            await StopAgentAsync(identity).ConfigureAwait(false);
        }
    }

    // jasperfx#535: a rebuild stops the projection's running continuous agents (below), replays through
    // transient rebuild agents, drains those, and returns WITHOUT restarting the continuous agents it
    // stopped. This is by contract: on a host with the store's own coordinator loop the coordinator
    // resurrects the stopped shards, and on a coordinator-less host (e.g. Wolverine-managed
    // event-subscription distribution) restoring continuous execution is the DRIVING CALLER's
    // responsibility after RebuildProjectionAsync returns — see Wolverine's EventSubscriptionAgent.
    // RebuildAsync. Restarting here unconditionally would double-start against a store coordinator.
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

            // wolverine#3520: register the restarted agent in the running set. Before this, rewind started
            // continuous agents that were never tracked in _agents: _agents still pointed at the agent
            // stopRunningAgents() had just HardStopped, StartAgentAsync(ShardName) could not find the live
            // agent, and any subsequent restart through the registered path spun up a DUPLICATE agent on
            // the same progression row. Under a store-owned coordinator this was masked; under
            // Wolverine-managed distribution (no coordinator) it left the shard effectively orphaned.
            await registerStartedAgentAsync(agent).ConfigureAwait(false);
        }
    }

    // wolverine#3520: adopt an already-started agent into the running set under the registry lock,
    // replacing any prior (now-stopped) registration for the same identity. Kept separate from
    // tryStartAgentAsync because the rewind path has already determined its own floor and started the
    // agent in Continuous mode; this only reconciles _agents and the tenant-polling set.
    private async Task registerStartedAgentAsync(ISubscriptionAgent agent)
    {
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            _agents = _agents.AddOrUpdate(agent.Name.Identity, agent);
            syncTenantPolling();
        }
        finally
        {
            _semaphore.Release();
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

