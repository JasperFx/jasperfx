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

// jasperfx#480 acceptance, as reshaped by jasperfx#598/#610: the opt-in blue/green side-effect gate.
// When a NEW version of a projection (ShardName.Version > 1) starts continuous execution behind the
// highest PRIOR version's persisted progression mark N, it catches up over [current..N] with side
// effects SUPPRESSED and only emits them past N — so RaiseSideEffects fires only for events the
// previous version never processed.
//
// The #598 change is where that suppression lives. It used to be a bounded replay run to completion
// INSIDE the agent start path, which meant a start that normally costs milliseconds cost tens of
// seconds to minutes and — because a host does not consider an agent assigned until its start returns
// — turned a whole cluster's assignment table into a progress bar for the catch-up. Now the agent
// starts immediately and carries the suppressed catch-up itself as ordinary continuous work.
//
// These tests drive the REAL JasperFxAsyncDaemon (real SubscriptionAgents, substituted store/database)
// with a recording execution: every page the daemon enqueues is captured along with whether the agent
// had side effects suppressed while it ran, so the [0..N] suppressed / (N..HWM] live boundary is
// asserted directly.
public class BlueGreenSideEffectGateTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private const long HighWater = 1500;

    // Recorded page: was the agent suppressing side effects while this page ran, and what did it span?
    private record Page(bool Suppressed, long Floor, long Ceiling);

    [Fact]
    public async Task a_gated_start_returns_immediately_with_the_agent_running_and_visibly_suppressed()
    {
        // THE issue (#598/#610). The warm-up has not run — the loader is held shut, so not one page has
        // been processed — and the start has nevertheless returned with a Running, registered, assignable
        // agent. Before #598 this call would not have returned until the entire replay to 1,000 finished.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);
        harness.Loader.HoldPages();

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Running);
        harness.Execution.RecordedPages.ShouldBeEmpty();

        // ...and an operator can tell this apart from a shard running normally, which before #598 was
        // impossible without reading pod logs: the shard simply did not exist yet.
        var started = await harness.NextStateAsync("Trips:V3:All", x => x.Action == ShardAction.Started);
        started.SideEffectsSuppressed.ShouldBeTrue();
        started.SideEffectGateMark.ShouldBe(1000);
    }

    [Fact]
    public async Task fresh_deploy_suppresses_side_effects_to_the_prior_version_mark_then_enables_them()
    {
        // V3 is freshly deployed (no progression of its own), V2 left off at 1,000. Everything up to
        // 1,000 runs suppressed; everything past it runs live.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 0L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 1000L, HighWater));

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Running);
        harness.ProgressFor("Trips:V3:All").ShouldBe(HighWater);
    }

    [Fact]
    public async Task no_page_ever_straddles_the_prior_version_mark()
    {
        // The correctness constraint that makes an in-flight suppression window work at all. With a mark
        // that is NOT on a batch boundary, a page spanning it would force a choice between re-emitting
        // side effects the prior version already emitted and dropping the ones only this version owes.
        // The agent clamps its loading ceiling to the mark instead, so the flip lands exactly on it.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options =>
            {
                options.GateSideEffectsBehindPriorVersion = true;
                options.BatchSize = 500;
            });
        harness.SetProgress("Trips:V2:All", 1234);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 0L, 500L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 500L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 1000L, 1234L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 1234L, 1500L));
    }

    [Fact]
    public async Task the_warm_up_never_routes_through_the_optimized_replay_executor()
    {
        // Store-implemented replay executors (Marten/Polecat) replay to their OWN detected high-water,
        // not to a custom ceiling. A freshly deployed gated version starts Continuous at 0, which is
        // exactly the trigger for the optimized-rebuild shortcut — so without a guard the shortcut would
        // run straight past the prior version's mark and hand off with nothing left to suppress. Before
        // jasperfx#598 this could not happen, because the warm-up had already pushed progression to the
        // mark before Continuous ever started; on the new shape it has to be guarded explicitly.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true, withReplayExecutor: true);
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 0L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 1000L, HighWater));

        harness.Execution.ReplayExecutorInvocations.ShouldBe(0);
    }

    [Fact]
    public async Task the_gate_applies_when_starting_a_single_agent_by_identity()
    {
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAgentAsync("Trips:V3:All", TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 0L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 1000L, HighWater));

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task an_interrupted_warm_up_resumes_the_suppressed_catch_up_from_its_own_progress()
    {
        // A crash mid-warm-up leaves the new version's progression at 400 < N (1,000). The gate triggers
        // on "behind the prior mark", not only on zero progress, so the restart suppresses side effects
        // for the remaining (400..1000] instead of re-emitting them. Crash safety is why the flip keys
        // off COMMITTED progression: it is exactly what this restart re-reads.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);
        harness.SetProgress("Trips:V3:All", 400);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 400L, 1000L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 1000L, HighWater));
    }

    [Fact]
    public async Task no_gate_without_the_opt_in()
    {
        // Same fresh-deploy state, but the projection did not opt in: today's behavior, one continuous
        // catch-up over the whole history with side effects firing throughout.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3));
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 0L, HighWater));
    }

    [Fact]
    public async Task no_gate_for_version_1()
    {
        await using var harness = new GateHarness(ShardName.Compose("Trips"),
            options => options.GateSideEffectsBehindPriorVersion = true);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 0L, HighWater));
    }

    [Fact]
    public async Task no_gate_when_the_new_version_is_already_past_the_prior_mark()
    {
        // Not a fresh deploy — V3 has its own progression ahead of V2's final mark, so this is a plain
        // resume and nothing may be suppressed.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);
        harness.SetProgress("Trips:V3:All", 1200);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 1200L, HighWater));
    }

    [Fact]
    public async Task no_gate_when_no_prior_version_progression_exists()
    {
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 0L, HighWater));
    }

    [Fact]
    public async Task the_gate_resolves_the_highest_prior_version_and_ignores_other_shards()
    {
        // V5 must warm up to V4's mark (the highest prior), not V2's — and rows for other tenants or
        // other projections must not leak into the resolution.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 5),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 800);
        harness.SetProgress("Trips:V4:All", 1200);
        harness.SetProgress("Trips:V4:All:tenant1", 5000);
        harness.SetProgress("Others:V4:All", 999);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(true, 0L, 1200L));
        (await harness.Execution.NextPageAsync()).ShouldBe(new Page(false, 1200L, HighWater));
    }

    [Fact]
    public async Task the_gate_is_skipped_for_from_present_subscriptions()
    {
        // FromPresent ignores persisted progression entirely and jumps to the live high-water, which is
        // incompatible with suppressing up to a persisted mark — the daemon skips the gate (warning) and
        // the shard starts exactly as it does today.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options =>
            {
                options.GateSideEffectsBehindPriorVersion = true;
                options.SubscribeFromPresent();
            });
        harness.SetProgress("Trips:V2:All", 1000);

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Running);

        // Give the command loop a beat: a wrongly-triggered gate would surface as a suppressed page.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        harness.Execution.RecordedPages.ShouldNotContain(x => x.Suppressed);
    }

    [Fact]
    public async Task a_failed_gate_resolution_leaves_the_shard_stopped_instead_of_emitting_side_effects()
    {
        // Without the prior version's mark there is no way to tell which events it already covered, and
        // starting continuous execution anyway would fire side effects over all of them — the exact bug
        // the opt-in exists to prevent. Nothing starts; the next start resolves the mark again.
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.SetProgress("Trips:V2:All", 1000);
        harness.FailPriorVersionLookup();

        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        harness.Daemon.StatusFor("Trips:V3:All").ShouldBe(AgentStatus.Stopped);
        harness.Execution.RecordedPages.ShouldBeEmpty();
        harness.ProgressFor("Trips:V3:All").ShouldBe(0);
    }

    [Fact]
    public async Task a_failed_gate_resolution_surfaces_its_cause_from_the_ShardName_start_overload()
    {
        await using var harness = new GateHarness(ShardName.Compose("Trips", version: 3),
            options => options.GateSideEffectsBehindPriorVersion = true);
        harness.FailPriorVersionLookup();

        var ex = await Should.ThrowAsync<ShardStartException>(() =>
            harness.Daemon.StartAgentAsync(ShardName.Compose("Trips", version: 3),
                TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<DivideByZeroException>();
    }

    // ---------------------------------------------------------------------------------------------
    // Harness: a REAL JasperFxAsyncDaemon over a substituted store + database with a single registered
    // shard. Progression is a mutable dictionary — the recording execution writes each acknowledged
    // page's ceiling back to it, so a restart reads the mark the suppressed catch-up just persisted,
    // exactly as a real store would behave.
    // ---------------------------------------------------------------------------------------------

    private sealed class GateHarness : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, long> _progress = new();
        private readonly ConcurrentBag<ShardState> _states = new();
        private volatile bool _failPriorVersionLookup;

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

            var tracker = new ShardStateTracker(new NulloLogger());
            tracker.Subscribe(new RecordingObserver(_states));
            database.Tracker.Returns(tracker);

            database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .Returns(info => Task.FromResult(_progress.GetValueOrDefault(info.Arg<ShardName>().Identity)));
            database.AllProjectionProgress(Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    if (_failPriorVersionLookup)
                    {
                        throw new DivideByZeroException("Configured progression lookup failure");
                    }

                    return Task.FromResult<IReadOnlyList<ShardState>>(
                        _progress.Select(pair => new ShardState(pair.Key, pair.Value)).ToList());
                });

            var projections = new FakeProjectionGraph { MaxConcurrentEventLoadsPerDatabase = 0 };

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                store, database, new NulloLogger(), detector, projections);
        }

        public StubPageLoader Loader { get; }
        public RecordingExecution Execution { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public void SetProgress(string shardIdentity, long sequence) => _progress[shardIdentity] = sequence;

        public long ProgressFor(string shardIdentity) => _progress.GetValueOrDefault(shardIdentity);

        public void FailPriorVersionLookup() => _failPriorVersionLookup = true;

        // jasperfx#609: ShardStateTracker publishes through a Block<ShardState>, so a subscribed observer
        // runs on the block's consumer thread and is NOT guaranteed to have run by the time the start
        // call returns. Reading the collected states straight after the call is a race — one CI run lost
        // it on the retired timeout suite — so wait for the state instead of snapshotting.
        public async Task<ShardState> NextStateAsync(string shardIdentity, Func<ShardState, bool> match)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                var hit = _states.FirstOrDefault(x => x.ShardName == shardIdentity && match(x));
                if (hit != null) return hit;

                if (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for a matching ShardState for '{shardIdentity}'");
                }

                await Task.Delay(20, CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Loader.ReleasePages();
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    private sealed class RecordingObserver : IObserver<ShardState>
    {
        private readonly ConcurrentBag<ShardState> _states;

        public RecordingObserver(ConcurrentBag<ShardState> states) => _states = states;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value) => _states.Add(value);
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

    // Serves densely-numbered pages spanning (request.Floor, request.HighWater] capped at the batch size,
    // so the recorded page boundaries ARE the assertion surface. The events are real (rather than empty
    // pages) so that CalculateCeiling actually honors the batch size — which is what lets a test place the
    // gate mark OFF a batch boundary and prove no page straddles it. HoldPages parks every load so a test
    // can observe the started-but-not-yet-warmed-up state.
    private sealed class StubPageLoader : IEventLoader
    {
        private volatile TaskCompletionSource? _hold;

        public void HoldPages() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleasePages() => _hold?.TrySetResult();

        public async Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            var hold = _hold;
            if (hold != null)
            {
                await hold.Task.WaitAsync(token);
            }

            var page = new EventPage(request.Floor);
            var target = Math.Min(request.Floor + request.BatchSize, request.HighWater);
            for (var sequence = request.Floor + 1; sequence <= target; sequence++)
            {
                page.Add(new Event<AEvent>(new AEvent()) { Sequence = sequence });
            }

            page.CalculateCeiling(request.BatchSize, request.HighWater);
            return page;
        }
    }

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

    // Acknowledges every page immediately (posting RangeCompleted back to the agent), records whether the
    // agent had side effects suppressed while the page ran, and persists the ceiling as the shard's
    // progression — the store-side behavior a restart depends on.
    private sealed class RecordingExecution : ISubscriptionExecution
    {
        private readonly GateHarness _harness;
        private readonly bool _withReplayExecutor;
        private readonly Channel<Page> _pages = Channel.CreateUnbounded<Page>();
        private int _replayExecutorInvocations;

        public RecordingExecution(GateHarness harness, ShardName shardName, bool withReplayExecutor)
        {
            _harness = harness;
            ShardName = shardName;
            _withReplayExecutor = withReplayExecutor;
        }

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

        public ConcurrentBag<Page> RecordedPages { get; } = new();

        public int ReplayExecutorInvocations => Volatile.Read(ref _replayExecutorInvocations);

        public async Task<Page> NextPageAsync()
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            return await _pages.Reader.ReadAsync(timeout.Token);
        }

        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
        {
            var entry = new Page(subscriptionAgent.SideEffectsSuppressed, page.Floor, page.Ceiling);
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

    // A plain, non-partitioned detector pinned at Mark: the initial StartAsync detection publishes it,
    // deterministically seeding Tracker.HighWaterMark before any agent starts.
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
