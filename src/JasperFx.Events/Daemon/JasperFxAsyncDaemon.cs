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
    // Not readonly: StopAllAsync drains (completes) the writer while the daemon is quiesced, then
    // rebuilds it so a subsequent StartAllAsync resumes persisting extended progression (jasperfx#557).
    //
    // jasperfx#621: NULL until this daemon is actually started. The Tracker is per-database and
    // SHARED, so subscribing a writer in the constructor meant every daemon ever built for a database
    // added another writer to the same publication stream -- each renting its own connection and
    // issuing the same UPDATE against the same rows. Building a daemon is a documented way to *read*
    // projection state (IDocumentStore.BuildProjectionDaemonAsync is a fresh instance per call, no
    // caching), and such a daemon must not acquire a background write loop as an invisible side effect.
    private ExtendedProgressionWriter? _extendedProgression;
    private IDisposable? _extendedProgressionSubscription;
    private RetryBlock<DeadLetterEvent> _deadLetterBlock;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    // marten#5055: StopAllAsync on an already-disposed daemon must be a no-op instead of throwing
    // ObjectDisposedException from _cancellation.Token. Volatile because Dispose() (sync, e.g. from
    // ProjectionCoordinatorBase.StopAsync) can race a StopAllAsync fanned out from another Pause/Stop.
    private volatile bool _disposed;

    // Only non-null when the backing store partitions events per tenant; null keeps the daemon on the
    // single store-global high-water mark (today's behavior, byte for byte). jasperfx#407 Phase 2b.
    private readonly TenantedHighWaterCoordinator? _tenantHighWater;

    // jasperfx#492: OnNext(HighWaterMark) only fires when the STORE-GLOBAL mark changes, so a lagging
    // tenant appending below the global max would never be re-polled in a quiet system. This timer
    // guarantees a per-tenant poll at least every SlowPollingTime; _lastTenantHighWaterPoll dedups it
    // against the OnNext-driven fast path so an active system still polls once per global tick.
    private readonly Timer? _tenantHighWaterTimer;
    private DateTimeOffset _lastTenantHighWaterPoll;

    // jasperfx#539: highest store-global mark that has already driven a per-tenant poll. OnNext re-triggers a
    // tenant poll only on a genuine advance past this, so the per-cycle high-water HEARTBEAT publications
    // (which carry the same, non-advancing mark) can't feed back into an unbounded poll → publish → poll loop.
    private long _lastTenantPollTriggerMark;

    // jasperfx#539: Path B (per-tenant) watchdog bookkeeping, mirroring HighWaterAgent's. Serializes
    // overlapping restarts and caps them to once per staleness window.
    private int _tenantHighWaterRemediating;
    private DateTimeOffset _lastTenantHighWaterRemediation;

    // jasperfx#494 (epic #486 WS2): shared by every agent loader this daemon builds so the
    // database's connection footprint stays O(databases), not O(agents). Null = unthrottled.
    private SemaphoreSlim? _loadThrottle;
    private int _maxConcurrentEventLoads;

    // Epic #486 WS3: bounds concurrent projection batch execute/commit sessions. Reaches the
    // executions through IDaemonRuntime -> SubscriptionAgent -> EventRange.Agent. Null = unbounded.
    private SemaphoreSlim? _batchWriteThrottle;
    private int _maxConcurrentBatchWrites;

    public SemaphoreSlim? BatchWriteThrottle => _batchWriteThrottle;

    // jasperfx#598/#610: bounds how many shards are simultaneously loading inside the blue/green
    // side-effect gate's warm-up window. Reaches the agents through IDaemonRuntime. Null = unbounded.
    private SemaphoreSlim? _warmupThrottle;
    private int _maxConcurrentWarmups;

    public SemaphoreSlim? SideEffectGateWarmupThrottle => _warmupThrottle;

    /// <summary>
    /// jasperfx#598/#610: the per-database cap on how many shards may be inside the blue/green
    /// side-effect gate's warm-up window and actively loading at once. Before #598 this number was
    /// emergent — whatever fraction of the distribution layer's in-flight agent-start chunk happened to
    /// need a gate — and an operator had no way to choose it. Null or a non-positive value is
    /// unbounded, which is the default now that a warm-up is ordinary catch-up work already paced by
    /// <see cref="MaxConcurrentEventLoadsPerDatabase"/> and <see cref="MaxConcurrentBatchWritesPerDatabase"/>.
    /// Like the other governors here, the previous semaphore is deliberately not disposed, since agents
    /// may still be waiting on it.
    /// </summary>
    public int MaxConcurrentSideEffectGateWarmupsPerDatabase
    {
        get => _maxConcurrentWarmups;
        set
        {
            _maxConcurrentWarmups = value;
            _warmupThrottle = value > 0 ? new SemaphoreSlim(value) : null;
        }
    }

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

        // jasperfx#621: the extended progression writer is armed on the start path, NOT here
        _deadLetterBlock = buildDeadLetterBlock();

        MaxConcurrentEventLoadsPerDatabase = _projections.MaxConcurrentEventLoadsPerDatabase;
        MaxConcurrentBatchWritesPerDatabase = _projections.MaxConcurrentBatchWritesPerDatabase;
        MaxConcurrentSideEffectGateWarmupsPerDatabase = _projections.MaxConcurrentSideEffectGateWarmupsPerDatabase;

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

        // jasperfx#621: the extended progression writer is armed on the start path, NOT here
        _deadLetterBlock = buildDeadLetterBlock();

        MaxConcurrentEventLoadsPerDatabase = _projections.MaxConcurrentEventLoadsPerDatabase;
        MaxConcurrentBatchWritesPerDatabase = _projections.MaxConcurrentBatchWritesPerDatabase;
        MaxConcurrentSideEffectGateWarmupsPerDatabase = _projections.MaxConcurrentSideEffectGateWarmupsPerDatabase;

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

    // jasperfx#537: the writer checks the store's ExtendedProgressionEnabled flag live per publication
    // so runtime opt-in is honored. Built through a helper so the start path can rebuild it after
    // StopAllAsync drained it, exactly as it rebuilds the dead-letter block (jasperfx#557).
    private ExtendedProgressionWriter buildExtendedProgressionWriter()
        => new(_store, Database, _store.TimeProvider,
            _loggerFactory?.CreateLogger<ExtendedProgressionWriter>() ?? Logger)
        {
            // jasperfx#622: off unless the application asks for it. This is the configuration path
            // the interval never had -- before #622 it was a hardcoded 5 seconds that no
            // DaemonSettings knob could reach.
            HeartbeatWriteInterval =
                _projections.ExtendedProgressionHeartbeatInterval ?? TimeSpan.Zero
        };

    /// <summary>
    /// jasperfx#621: arm the extended progression writer. Called from every path that actually starts
    /// this daemon's agents -- and from nowhere else, so a daemon built purely to inspect state
    /// (CurrentAgents(), Tracker reads) never subscribes a telemetry writer to the database's SHARED
    /// tracker, and never writes to the database at all. Idempotent: repeated starts re-use the
    /// armed writer rather than stacking a second subscription on the same tracker.
    /// </summary>
    private void armExtendedProgressionWriter()
    {
        if (_disposed || _extendedProgressionSubscription != null) return;

        _extendedProgression = buildExtendedProgressionWriter();
        _extendedProgressionSubscription = Tracker.Subscribe(_extendedProgression);
    }

    // jasperfx#621: unsubscribe from the shared tracker and drain. Split out because Dispose() (sync)
    // and StopAllAsync (async, where the drain can be awaited) both need it, and both must leave the
    // daemon disarmed so a later start re-arms a fresh writer rather than resurrecting a completed one.
    private ExtendedProgressionWriter? detachExtendedProgressionWriter()
    {
        var writer = _extendedProgression;
        _extendedProgressionSubscription?.Dispose();
        _extendedProgressionSubscription = null;
        _extendedProgression = null;
        return writer;
    }

    public IEventDatabase Database { get; }

    public ILogger Logger { get; }

    public void Dispose()
    {
        // marten#5055: idempotent, and flags StopAllAsync to no-op once the daemon is gone. Double
        // disposal is a real path: ProjectionCoordinatorBase.StopAsync disposes every resolved daemon,
        // and a second Pause/Stop (double hosted-service registration, user pause + host stop) fans
        // back out over the same instances.
        if (_disposed) return;
        _disposed = true;

        _cancellation?.Dispose();
        _highWater?.Dispose();
        _tenantHighWaterTimer?.Stop();
        _tenantHighWaterTimer?.Dispose();
        _breakSubscription.Dispose();
        // Completes the writer's queue so a final Stopped write can drain in the background. Null
        // when this daemon was never started (jasperfx#621) -- nothing to unsubscribe or drain.
        var writer = detachExtendedProgressionWriter();
        if (writer != null)
        {
            _ = writer.DisposeAsync();
        }
        _deadLetterBlock.Dispose();
        _loadThrottle?.Dispose();
        _batchWriteThrottle?.Dispose();
        _warmupThrottle?.Dispose();
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


    private async Task<bool> tryStartAgentAsync(ISubscriptionAgent agent, ShardExecutionMode mode,
        long sideEffectGateMark = 0)
    {
        // jasperfx#621: this daemon is about to own a running agent, so it owns that agent's telemetry.
        // Every continuous start path funnels through here; a daemon built only to read state does not.
        armExtendedProgressionWriter();

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

            // jasperfx#598/#610: the blue/green side-effect gate is now armed ON the agent instead of
            // being run to completion before it. The agent starts here and now — assignable, observable,
            // heartbeating — and carries the suppressed warm-up to the prior version's mark as ordinary
            // catch-up work. The mark is only meaningful if it is actually ahead of where this version
            // resumes from; the daemon deliberately does not re-read progression to check that itself,
            // because position.Floor is the authoritative answer and it was just computed above.
            if (sideEffectGateMark > position.Floor)
            {
                request = request with { SideEffectGateMark = sideEffectGateMark };

                Logger.LogInformation(
                    "Projection shard {Name} v{Version} is behind the prior version's progression ({Current} < {Prior}); it starts continuous execution immediately with side effects suppressed and enables them on reaching {Prior} (blue/green side-effect gate)",
                    agent.Name.Identity, agent.Name.Version, position.Floor, sideEffectGateMark,
                    sideEffectGateMark);
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
    private async Task rebuildAgent(ISubscriptionAgent agent, long highWaterMark, TimeSpan shardTimeout)
    {
        // jasperfx#621: a rebuild agent publishes real status transitions for this daemon's shards
        armExtendedProgressionWriter();

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

            var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Rebuild, errorOptions, this);
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

    // jasperfx#480/#598/#610: single entry point for starting a shard in Continuous mode so every start
    // path (StartAllAsync, StartAgentAsync by name, the per-tenant fan-outs) applies the opt-in blue/green
    // side-effect gate. Returns true when the continuous agent was actually started.
    //
    // Until #598 this method BLOCKED on the gate: it ran a bounded, side-effect-suppressed replay to the
    // prior version's mark and only started the agent afterwards. At scale that made a start that normally
    // costs milliseconds cost tens of seconds to minutes — 27s p50 / 82s p95 / 215s tail across 993 tenant
    // shards, ~200 minutes before every shard had started once — and, because an agent does not count as
    // assigned until its start returns, it made the whole cluster's assignment table a progress bar for the
    // catch-up rather than for the distribution. Now the gate only RESOLVES the mark here (one read) and
    // hands it to the agent, which starts immediately and carries the suppressed catch-up itself.
    private async Task<bool> startContinuousShardAsync(AsyncShard<TOperations, TQuerySession> shard)
    {
        long gateMark;
        try
        {
            gateMark = await resolveSideEffectGateMarkAsync(shard).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Without the prior version's mark there is no way to tell which events the previous version
            // already covered, and starting continuous execution anyway would emit side effects over all
            // of them — the exact bug the opt-in exists to prevent. Leave the shard stopped; the next
            // start resolves the mark again and resumes from this version's persisted progress.
            Logger.LogError(e,
                "Error resolving the blue/green side-effect gate mark for projection shard {Name}. The shard is left stopped rather than started with side effects enabled over history the prior version already covered",
                shard.Name.Identity);
            _lastStartFailures = _lastStartFailures.AddOrUpdate(shard.Name.Identity, e);
            return false;
        }

        var agent = buildAgentForShard(shard);
        var started = await tryStartAgentAsync(agent, ShardExecutionMode.Continuous, gateMark)
            .ConfigureAwait(false);

        if (!started && agent is IAsyncDisposable d)
        {
            await d.DisposeAsync().ConfigureAwait(false);
        }

        return started;
    }

    // jasperfx#480: opt-in blue/green side-effect gate. When a projection opts in and a NEW version of it
    // starts behind the highest PRIOR version's persisted progression mark N, the agent runs from its own
    // progress to N with side effects suppressed and only emits them past N — so RaiseSideEffects fires
    // only for events the previous version never processed. Returns N, or 0 when the gate does not apply.
    //
    // Crash safety is unchanged from the pre-#598 shape and comes from the same place: the trigger is
    // "persisted progress < N", not "no progress at all", so an interrupted warm-up resumes suppressed
    // over whatever is left of (progress, N] instead of re-emitting what it already covered.
    private async Task<long> resolveSideEffectGateMarkAsync(AsyncShard<TOperations, TQuerySession> shard)
    {
        var name = shard.Name;
        if (!shard.Options.GateSideEffectsBehindPriorVersion || name.Version <= 1)
        {
            return 0;
        }

        if (shard.Options.UsesFromPresent(Database))
        {
            Logger.LogWarning(
                "Projection shard {Name} opts into GateSideEffectsBehindPriorVersion but subscribes from 'present', which ignores persisted progression. The side-effect gate is skipped",
                name.Identity);
            return 0;
        }

        return await resolvePriorVersionProgressAsync(name, _cancellation.Token).ConfigureAwait(false);
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
            //
            // Pin rather than Activate: syncTenantPolling() rebuilds the polled set from the agents
            // REGISTERED on this node, and this agent is not registered until its start completes. Any
            // concurrent start or stop that reconciles in the meantime would drop this tenant again, and
            // the poll below would then silently skip it — leaving the agent to be seeded with no
            // ceiling at all (an unrecoverable pause for a catch-up shard, a full-history replay for a
            // FromPresent one). Field-observed under Wolverine-managed distribution with 25-way start
            // parallelism and thousands of agents per node.
            _tenantHighWater.PolledTenants.Pin(requested.TenantId);

            bool tenantStarted;
            try
            {
                await pollTenantHighWaterAsync().ConfigureAwait(false);

                var tenantShard = baseShard with { Name = baseShard.Name.ForTenant(requested.TenantId) };
                tenantStarted = await startContinuousShardAsync(tenantShard).ConfigureAwait(false);
            }
            finally
            {
                _tenantHighWater.PolledTenants.Unpin(requested.TenantId);
            }

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
    // agent) and, less often since jasperfx#598 moved the warm-up off the start path, a failure to
    // resolve the blue/green side-effect gate mark.
    private string describeStartFailure(ShardName name)
    {
        if (_agents.TryFind(name.Identity, out var existing))
        {
            return $"An agent is registered for this shard in status '{existing.Status}' rather than running. It was most likely paused by an error; check the log for the pause reason and restart the shard once resolved.";
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

    // jasperfx#564: the graceful drain of a shard used to be bounded by a hardcoded 5 seconds. An
    // in-flight page that needs longer than that to finish and flush progression got cancelled
    // mid-flush, abandoning the progression write -> ProgressionProgressOutOfOrderException on the
    // next start. The bound is now DaemonSettings.StopAndDrainTimeout, and a non-positive value or
    // Timeout.InfiniteTimeSpan opts out of the separate bound entirely (the daemon's own
    // _cancellation still applies through the linked source at every call site).
    private CancellationTokenSource stopAndDrainCancellation()
    {
        var cancellation = new CancellationTokenSource();
        var timeout = _projections.StopAndDrainTimeout;
        if (timeout > TimeSpan.Zero)
        {
            cancellation.CancelAfter(timeout);
        }

        return cancellation;
    }

    private async Task stopIfRunningAsync(string shardIdentity)
    {
        if (_agents.TryFind(shardIdentity, out var agent))
        {
            using var cancellation = stopAndDrainCancellation();
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
                using var cancellation = stopAndDrainCancellation();
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
        // marten#5055: a disposed daemon has nothing left to stop. Without this guard, the second
        // Pause/Stop pass at shutdown (double AddAsyncDaemon registration, user pause + host stop,
        // Wolverine quiesce + host stop) hits _cancellation.Token on a disposed source and throws
        // ObjectDisposedException, which the coordinator then logs as an Error per daemon.
        if (_disposed) return;

        try
        {
            await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose() raced this stop between the flag check and the token access; same benign
            // "already disposed" outcome as the early return above.
            return;
        }

        try
        {
            await _highWater.StopAsync().ConfigureAwait(false);

            using var cancellation = stopAndDrainCancellation();
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

            // jasperfx#557: drain the extended-progression writer here, on the async stop path where the
            // agents are already stopped-and-drained and the daemon is genuinely quiesced, so a Stopped
            // heartbeat queued during shutdown is fully persisted before StopAllAsync returns. Left to the
            // sync Dispose() it would drain fire-and-forget in the background and could overtake a later
            // deliberate write to the same progression row.
            try
            {
                var writer = detachExtendedProgressionWriter();
                if (writer != null)
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to drain the extended progression writer");
            }

            _agents = ImHashMap<string, ISubscriptionAgent>.Empty;
            syncTenantPolling();

            _cancellation.TryReset();
            _deadLetterBlock = buildDeadLetterBlock();

            // jasperfx#621: deliberately NOT resubscribed here. A stopped daemon writes no telemetry;
            // the next start path re-arms a fresh writer (armExtendedProgressionWriter), which is where
            // the dead-letter block's eager rebuild above and this part company.
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task StartHighWaterDetectionAsync()
    {
        // jasperfx#621: this daemon is genuinely starting, so it owns its shards' telemetry
        armExtendedProgressionWriter();

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

    // jasperfx#539: delegate the high-water liveness surface to whichever path is active. Under per-tenant
    // partitioning the store-global loop is skipped and the tenant coordinator owns liveness (Path B);
    // otherwise it's the store-global HighWaterAgent (Path A).
    public DateTimeOffset? HighWaterLastPolledAt =>
        _tenantHighWater != null ? _tenantHighWater.LastPolledAt : _highWater.LastPolledAt;

    public bool IsHighWaterStale
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return _tenantHighWater != null
                ? _tenantHighWater.IsStale(_projections.HighWaterStalenessThreshold, now)
                : _highWater.IsStale(_projections.HighWaterStalenessThreshold, now);
        }
    }

    public async Task RestartHighWaterAgentAsync(CancellationToken token)
    {
        if (_tenantHighWater != null)
        {
            await publishHighWaterStatusAsync(ShardAction.Restarted, "Restarted").ConfigureAwait(false);
            restartTenantHighWater();
            return;
        }

        await _highWater.RestartAsync().ConfigureAwait(false);
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

            // jasperfx#539: only a genuine advance of the store-global mark drives a per-tenant poll. Without
            // this guard the high-water heartbeat (published every cycle carrying the SAME mark) would loop
            // back through here into pollTenantHighWaterAsync, which publishes another heartbeat, forever.
            if (_tenantHighWater != null && value.Sequence > _lastTenantPollTriggerMark)
            {
                _lastTenantPollTriggerMark = value.Sequence;

                // Reuse the global high-water cadence to drive one vectorized per-tenant poll.
                // jasperfx#644: through the coalesced path — this fire-and-forget used to start a brand-new
                // full cycle per global-mark advance with no in-flight guard, and at thousands of tenants a
                // cycle is slower than the advance rate, so concurrent cycles stacked up without bound.
                _ = pollTenantHighWaterCoalescedAsync();
            }
        }

        _shardStateTracker?.Add(value);
    }

    // jasperfx#644: the background-trigger flavor (OnNext fast path + cadence timer). Coalesces into the
    // coordinator's single-flight cycle instead of stacking a concurrent full cycle per trigger. The
    // heartbeat is published only by the call that actually ran a cycle — a coalesced trigger's work is
    // covered by the in-flight cycle's own trailing rerun and heartbeat.
    private async Task pollTenantHighWaterCoalescedAsync()
    {
        if (_tenantHighWater == null)
        {
            return;
        }

        _lastTenantHighWaterPoll = DateTimeOffset.UtcNow;

        try
        {
            var ran = await _tenantHighWater
                .PollAndRouteCoalescedAsync(CurrentAgents, _cancellation.Token)
                .ConfigureAwait(false);

            if (ran)
            {
                await publishHighWaterStatusAsync(ShardAction.Updated, "Running").ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Error polling per-tenant high water for database {Name}", Database.Identifier);
        }
    }

    // The awaited flavor for priming paths (StartAgentAsync/StartAllAsync/rebuild ceilings) that need a
    // COMPLETED poll covering a just-activated tenant — these must not coalesce into a cycle that took its
    // tenant snapshot before the activation. jasperfx#644: bounded by their callers (operator/startup
    // driven); an overlapping background cycle is retired by the coordinator's epoch supersession.
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

            // jasperfx#539: publish the per-cycle liveness heartbeat for Path B. The coordinator has already
            // stamped its in-memory LastPolledAt; this surfaces the same beat on the live Tracker so
            // in-process consumers can tell "no new events" from "the tenant high-water poll died".
            // Carries the store-global mark unchanged, so it never advances it and, by the OnNext guard
            // above, never re-triggers a poll.
            //
            // jasperfx#622: this beat does NOT reach the ExtendedProgression columns, and never did --
            // ExtendedProgressionWriter.OnNext drops HighWaterMark and AllProjections states outright
            // (pinned by skips_high_water_mark_and_all_projections_states). The live Tracker is the only
            // place it shows up.
            await publishHighWaterStatusAsync(ShardAction.Updated, "Running").ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Error polling per-tenant high water for database {Name}", Database.Identifier);
        }
    }

    // jasperfx#539: publish a HighWaterMark ShardState carrying a liveness/status beat without moving the
    // mark. Used by the Path B heartbeat and the Path B watchdog (Faulted/Restarted). The OnNext re-trigger
    // guard keeps a same-mark publication from feeding back into another tenant poll.
    private ValueTask publishHighWaterStatusAsync(ShardAction action, string status)
    {
        return Tracker.PublishAsync(new ShardState(ShardState.HighWaterMark, Tracker.HighWaterMark)
        {
            Action = action,
            AgentStatus = status,
            LastHeartbeat = DateTimeOffset.UtcNow
        });
    }

    // jasperfx#539: Path B restart seam. The per-tenant path is timer-driven, so remediation is a stop/re-arm
    // of the cadence timer plus clearing any wedged in-flight guard so a fresh poll can start. A hung poll is
    // abandoned, mirroring HighWaterAgent's non-blocking restart — and jasperfx#644: "abandoned" now means
    // the coordinator bumps its cycle epoch so the wedged cycle actually retires itself when it wakes,
    // instead of grinding on as a leaked concurrent full cycle (one per staleness window was the OOM).
    private void restartTenantHighWater()
    {
        if (_tenantHighWaterTimer == null)
        {
            return;
        }

        _tenantHighWater?.AbandonInFlightPoll();
        _tenantHighWaterTimer.Stop();

        if (!_cancellation.IsCancellationRequested)
        {
            syncTenantHighWaterInterval();
            _tenantHighWaterTimer.Start();
        }
    }

    // jasperfx#539: Path B watchdog, fired off the tenant cadence timer. Restart the per-tenant poll when it
    // has stopped completing cycles within the staleness threshold. Capped to once per window and never
    // overlapping, exactly like HighWaterAgent.checkState governs Path A.
    private async Task checkTenantHighWaterStalenessAsync()
    {
        if (_tenantHighWater == null || !_highWater.IsRunning || _cancellation.IsCancellationRequested)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_tenantHighWater.IsStale(_projections.HighWaterStalenessThreshold, now))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _tenantHighWaterRemediating, 1, 0) == 1)
        {
            return;
        }

        try
        {
            if (_lastTenantHighWaterRemediation != default &&
                now - _lastTenantHighWaterRemediation < _projections.HighWaterStalenessThreshold)
            {
                return;
            }

            _lastTenantHighWaterRemediation = now;

            Logger.LogWarning(
                "Per-tenant high-water poll for database {Name} has not completed a cycle within {Threshold}; restarting the tenant high-water poll",
                Database.Identifier, _projections.HighWaterStalenessThreshold);

            await publishHighWaterStatusAsync(ShardAction.Restarted, "Restarted").ConfigureAwait(false);
            restartTenantHighWater();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Error restarting per-tenant high water for database {Name}", Database.Identifier);
        }
        finally
        {
            Volatile.Write(ref _tenantHighWaterRemediating, 0);
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
            _ = checkTenantHighWaterStalenessAsync();
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

        // jasperfx#644: single-flight (plus one trailing rerun) now lives in the coordinator, shared with
        // the OnNext fast path, so no trigger source can stack a concurrent full cycle.
        await pollTenantHighWaterCoalescedAsync().ConfigureAwait(false);
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
        // jasperfx#621: a rebuild runs real agents that publish real status transitions
        armExtendedProgressionWriter();

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

