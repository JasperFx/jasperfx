using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#480 acceptance: the opt-in blue/green side-effect gate. When a NEW version of a
// projection (ShardName.Version > 1) starts continuous execution behind the highest PRIOR version's
// persisted progression mark N, the daemon first replays to N in Rebuild mode (side effects
// suppressed) and only then starts Continuous from N — so RaiseSideEffects only fires for events the
// previous version never processed. These tests drive the REAL JasperFxAsyncDaemon (real
// SubscriptionAgents, substituted store/database) with a recording execution: every page the daemon
// enqueues is captured with the execution mode it ran under, so the [0..N] = Rebuild / (N..HWM] =
// Continuous boundary is asserted directly.
public class BlueGreenSideEffectGateTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private const long HighWater = 1500;

    [Fact]
    public async Task fresh_deploy_replays_to_the_prior_version_mark_without_side_effects_then_continues()
    {
        // The issue's headline scenario: V3 is freshly deployed (no progression of its own), V2 left
        // off at 1,000. Phase 1 must be a Rebuild-mode replay of [0..1000]; phase 2 Continuous from
        // 1,000 to the high-water.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Rebuild, 0L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 1000L, HighWater));

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Running);
        harness.ProgressFor("Trips:V3:All").ShouldBe(HighWater);
    }

    [Fact]
    public async Task the_gate_applies_when_starting_a_single_agent_by_identity()
    {
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAgentAsync("Trips:V3:All", CancellationToken.None).WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Rebuild, 0L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 1000L, HighWater));

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task an_interrupted_warm_up_resumes_the_suppressed_replay_from_its_own_progress()
    {
        // A crash mid-warm-up leaves the new version's progression at 400 < N (1,000). The gate
        // triggers on "behind the prior mark", not only on zero progress, so the restart suppresses
        // side effects for the remaining (400..1000] instead of re-emitting them.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);
        harness.SetProgress("Trips:V3:All", 400);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Rebuild, 400L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 1000L, HighWater));
    }

    [Fact]
    public async Task no_gate_without_the_opt_in()
    {
        // Same fresh-deploy state, but the projection did not opt in: today's behavior, one
        // continuous catch-up over the whole history (side effects firing throughout).
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3));
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 0L, HighWater));
    }

    [Fact]
    public async Task no_gate_for_version_1()
    {
        await using var harness = new GateHarness(ShardName.Compose("Trips"),
            options => options.GateSideEffectsBehindPriorVersion = true);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 0L, HighWater));
    }

    [Fact]
    public async Task no_gate_when_the_new_version_is_already_past_the_prior_mark()
    {
        // Not a fresh deploy — V3 has its own progression ahead of V2's final mark, so this is a
        // plain resume and the gate must not add a rebuild phase.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);
        harness.SetProgress("Trips:V3:All", 1200);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 1200L, HighWater));
    }

    [Fact]
    public async Task no_gate_when_no_prior_version_progression_exists()
    {
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 0L, HighWater));
    }

    [Fact]
    public async Task the_gate_resolves_the_highest_prior_version_and_ignores_other_shards()
    {
        // V5 must warm up to V4's mark (the highest prior), not V2's — and rows for other tenants
        // or other projections must not leak into the resolution.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 5),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 800);
        harness.SetProgress("Trips:V4:All", 1200);
        harness.SetProgress("Trips:V4:All:tenant1", 5000);
        harness.SetProgress("Others:V4:All", 999);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Rebuild, 0L, 1200L));
        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 1200L, HighWater));
    }

    [Fact]
    public async Task the_gate_is_skipped_for_from_present_subscriptions()
    {
        // FromPresent ignores persisted progression entirely and jumps to the live high-water, which
        // is incompatible with replaying to a persisted mark — the daemon skips the gate (warning)
        // and the shard starts exactly as it does today.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options =>
            {
                options.GateSideEffectsBehindPriorVersion = true;
                options.SubscribeFromPresent();
            });
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Running);

        // Give the command loop a beat: a wrongly-triggered warm-up would surface as a Rebuild page.
        await Task.Delay(100);
        harness.Execution.RecordedPages.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_warm_up_never_routes_through_the_optimized_replay_executor()
    {
        // Store-implemented replay executors (Marten/Polecat) are not guaranteed to honor a custom
        // ceiling — they replay to their own detected high-water, which would overshoot N and skip
        // the (N..HWM] side effects. The warm-up must use the plain loader path even when an
        // optimized executor is available.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true, withReplayExecutor: true);
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Rebuild, 0L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe((ShardExecutionMode.Continuous, 1000L, HighWater));

        harness.Execution.ReplayExecutorInvocations.ShouldBe(0);
    }

    [Fact]
    public async Task a_failed_warm_up_leaves_the_shard_stopped_instead_of_emitting_side_effects()
    {
        // If the suppressed warm-up fails, starting Continuous anyway would fire side effects over
        // history the prior version already covered — the exact bug the opt-in exists to prevent.
        // The shard stays paused (carrying the failure for observers); its partial progress makes
        // the next start resume the warm-up.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);
        harness.Loader.ThrowOnLoad = true;

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout);

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Paused);
        harness.Execution.RecordedPages.ShouldBeEmpty();
        harness.ProgressFor("Trips:V3:All").ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------------------
    // Harness: a REAL JasperFxAsyncDaemon over a substituted store + database with a single
    // registered shard. Progression is a mutable dictionary — the recording execution writes each
    // acknowledged page's ceiling back to it, so the continuous hand-off reads the mark the warm-up
    // replay just persisted, exactly as a real store would behave.
    // ---------------------------------------------------------------------------------------------

    private sealed class GateHarness : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, long> _progress = new();

        public GateHarness(ShardName shardName, Action<AsyncOptions>? configureOptions = null,
            bool withReplayExecutor = false)
        {
            Loader = new StubPageLoader();

            var detector = new StubDetector { Mark = HighWater };

            var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            store.Meter.Returns(new Meter("tests"));
            store.TimeProvider.Returns(TimeProvider.System);
            store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            store.RebuildErrors.Returns(new ErrorHandlingOptions());
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(Loader);

            var options = new AsyncOptions { BatchSize = 10_000 };
            configureOptions?.Invoke(options);

            Execution = new RecordingExecution(this, shardName, withReplayExecutor);
            var shard = new AsyncShard<FakeOperations, FakeSession>(options, ShardRole.Projection, shardName,
                new SingleExecutionFactory(Execution), new EventFilterable());
            store.AllShards().Returns([shard]);

            var database = Substitute.For<IEventDatabase>();
            database.Identifier.Returns("db1");
            database.DatabaseUri.Returns(new Uri("fake://db1"));
            database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));
            database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .Returns(info => Task.FromResult(_progress.GetValueOrDefault(info.Arg<ShardName>().Identity)));
            database.AllProjectionProgress(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<ShardState>>(
                    _progress.Select(pair => new ShardState(pair.Key, pair.Value)).ToList()));

            var projections = new FakeProjectionGraph { MaxConcurrentEventLoadsPerDatabase = 0 };

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                store, database, new NulloLogger(), detector, projections);
        }

        public StubPageLoader Loader { get; }
        public RecordingExecution Execution { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public void SetProgress(string shardIdentity, long sequence) => _progress[shardIdentity] = sequence;

        public long ProgressFor(string shardIdentity) => _progress.GetValueOrDefault(shardIdentity);

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    private sealed class FakeProjectionGraph :
        ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeProjectionGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }

    // Serves empty, already-caught-up pages spanning (request.Floor, request.HighWater] so a single
    // load completes each phase; the recorded page boundaries ARE the assertion surface.
    private sealed class StubPageLoader : IEventLoader
    {
        public bool ThrowOnLoad { get; set; }

        public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            if (ThrowOnLoad)
            {
                throw new DivideByZeroException("Configured loader failure");
            }

            var page = new EventPage(request.Floor);
            page.CalculateCeiling(request.BatchSize, request.HighWater);
            return Task.FromResult(page);
        }
    }

    // Hands the SAME execution to every agent the daemon builds for the shard (warm-up + continuous
    // run sequentially, never concurrently), so one recorder observes the full phase sequence.
    private sealed class SingleExecutionFactory : ISubscriptionFactory<FakeOperations, FakeSession>
    {
        private readonly RecordingExecution _execution;

        public SingleExecutionFactory(RecordingExecution execution)
        {
            _execution = execution;
        }

        public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
            IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName) => _execution;

        public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
            IEventDatabase database, ILogger logger, ShardName shardName) => _execution;
    }

    // Acknowledges every page immediately (posting RangeCompleted back to the agent), records the
    // (mode, floor, ceiling) it ran under, and persists the ceiling as the shard's progression —
    // the store-side behavior the continuous hand-off depends on.
    private sealed class RecordingExecution : ISubscriptionExecution
    {
        private readonly GateHarness _harness;
        private readonly bool _withReplayExecutor;
        private readonly Channel<(ShardExecutionMode, long, long)> _pages =
            Channel.CreateUnbounded<(ShardExecutionMode, long, long)>();
        private int _replayExecutorInvocations;

        public RecordingExecution(GateHarness harness, ShardName shardName, bool withReplayExecutor)
        {
            _harness = harness;
            ShardName = shardName;
            _withReplayExecutor = withReplayExecutor;
        }

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

        public ConcurrentBag<(ShardExecutionMode, long, long)> RecordedPages { get; } = new();

        public int ReplayExecutorInvocations => Volatile.Read(ref _replayExecutorInvocations);

        public async Task<(ShardExecutionMode, long, long)> NextPageAsync()
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            return await _pages.Reader.ReadAsync(timeout.Token);
        }

        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
        {
            var entry = (Mode, page.Floor, page.Ceiling);
            RecordedPages.Add(entry);
            _pages.Writer.TryWrite(entry);
            _harness.SetProgress(subscriptionAgent.Name.Identity, page.Ceiling);
            return subscriptionAgent.MarkSuccessAsync(page.Ceiling);
        }

        public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;

        public Task HardStopAsync() => Task.CompletedTask;

        public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
        {
            if (!_withReplayExecutor)
            {
                executor = null;
                return false;
            }

            executor = new CountingReplayExecutor(this);
            return true;
        }

        public Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
            CancellationToken cancellation) => Task.CompletedTask;

        public Task ProcessRangeAsync(EventRange range) => Task.CompletedTask;

        public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
        {
            caching = null;
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class CountingReplayExecutor : IReplayExecutor
        {
            private readonly RecordingExecution _parent;

            public CountingReplayExecutor(RecordingExecution parent)
            {
                _parent = parent;
            }

            public Task StartAsync(SubscriptionExecutionRequest request, ISubscriptionController controller,
                CancellationToken cancellation)
            {
                Interlocked.Increment(ref _parent._replayExecutorInvocations);
                return Task.CompletedTask;
            }
        }
    }

    // A plain, non-partitioned detector pinned at Mark: the initial StartAsync detection publishes
    // it, deterministically seeding Tracker.HighWaterMark before any agent starts.
    private sealed class StubDetector : IHighWaterDetector
    {
        public long Mark { get; set; }

        public Uri DatabaseUri { get; } = new("fake://db1");

        public bool SupportsTenantPartitioning => false;

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics
            {
                CurrentMark = Mark, LastMark = Mark, HighestSequence = Mark
            });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => throw new NotSupportedException();

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => throw new NotSupportedException();
    }
}
