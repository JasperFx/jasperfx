using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#1022: a stopped SubscriptionAgent must not outlive its daemon. StartAsync arms a recurring 10s heartbeat
// System.Threading.Timer, and only DisposeAsync disposed it -- but a graceful stop (StopAndDrainAsync,
// the path StopAllAsync, StopAgentAsync and every rebuild take) does not dispose the agent: the daemon
// just dropped it from its registry. The runtime's timer queue then rooted the timer, the timer's
// callback rooted the agent, and the agent rooted its tracker, execution, loader and through them the
// whole store and the host's service provider. A process that boots and stops many hosts (any test
// suite with a host per class) kept every one of them: one Fisher-backed suite grew to 13 GB and was
// OOM-killed.
//
// Two halves, each tested on its own: a drained agent stops its heartbeat whether or not anything
// disposes it, and the daemon disposes every agent it drops.
public class StoppedAgentReleaseTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task a_running_agent_is_held_by_its_heartbeat()
    {
        // The control: without it the collectible assertions below could pass by never having been able
        // to see a leak at all.
        using var tracker = new ShardStateTracker(new NulloLogger());
        var reference = await startAsync(tracker, drain: false);

        (await isCollectedAsync(reference)).ShouldBeFalse();

        await ((SubscriptionAgent)reference.Target!).DisposeAsync();
    }

    [Fact]
    public async Task a_drained_agent_is_collectible_with_nothing_disposing_it()
    {
        using var tracker = new ShardStateTracker(new NulloLogger());
        var reference = await startAsync(tracker, drain: true);

        (await isCollectedAsync(reference)).ShouldBeTrue();
    }

    [Fact]
    public async Task a_disposed_agent_is_collectible()
    {
        using var tracker = new ShardStateTracker(new NulloLogger());
        var reference = await startAsync(tracker, drain: false, dispose: true);

        (await isCollectedAsync(reference)).ShouldBeTrue();
    }

    [Fact]
    public async Task stop_agent_disposes_the_agent_it_drops()
    {
        await using var harness = new DaemonHarness();
        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);

        await harness.Daemon.StopAgentAsync("Trip:All").WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        harness.Daemon.CurrentAgents().ShouldBeEmpty();
        harness.Execution.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task stop_all_disposes_every_agent_it_drops()
    {
        await using var harness = new DaemonHarness();
        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);

        await harness.Daemon.StopAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        harness.Daemon.CurrentAgents().ShouldBeEmpty();
        harness.Execution.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task dispose_disposes_the_agents_a_daemon_still_holds()
    {
        // A host that disposes its daemon without stopping it first
        await using var harness = new DaemonHarness();
        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);

        harness.Daemon.Dispose();

        await harness.Execution.WaitForDisposalAsync(TestTimeout);
        harness.Daemon.CurrentAgents().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_stopped_daemon_holds_no_agent_alive()
    {
        await using var harness = new DaemonHarness();
        var reference = await startThroughDaemonAsync(harness);

        await harness.Daemon.StopAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        (await isCollectedAsync(reference)).ShouldBeTrue();
    }

    // Not inlined, so no local of the caller's state machine can keep the agent alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> startAsync(ShardStateTracker tracker, bool drain, bool dispose = false)
    {
        var agent = new SubscriptionAgent(new ShardName("Trip"), new AsyncOptions(), TimeProvider.System,
            caughtUpLoader(), new RecordingExecution(new ShardName("Trip")), tracker,
            Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);

        await agent.StartAsync(new SubscriptionExecutionRequest(0, ShardExecutionMode.Continuous,
            new ErrorHandlingOptions(), Substitute.For<IDaemonRuntime>()));

        if (drain) await agent.StopAndDrainAsync(CancellationToken.None);
        if (dispose) await agent.DisposeAsync();

        return new WeakReference(agent);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> startThroughDaemonAsync(DaemonHarness harness)
    {
        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);
        return new WeakReference(harness.Daemon.CurrentAgents().Single());
    }

    // The agent's command loop and the last page's continuations can still be unwinding when the stop
    // returns, so give them a few passes rather than reading one collection as the verdict.
    private static async Task<bool> isCollectedAsync(WeakReference reference)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (!reference.IsAlive) return true;

            await Task.Delay(50);
        }

        return false;
    }

    // An empty, already-caught-up page keeps a started agent healthy with no real storage behind it.
    private static IEventLoader caughtUpLoader()
    {
        var loader = Substitute.For<IEventLoader>();
        loader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<EventRequest>();
                var page = new EventPage(request.Floor);
                page.CalculateCeiling(request.BatchSize, request.HighWater);
                return Task.FromResult(page);
            });

        return loader;
    }

    private sealed class DaemonHarness : IAsyncDisposable
    {
        public DaemonHarness()
        {
            Execution = new RecordingExecution(new ShardName("Trip"));

            var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ILogger>(), Arg.Any<ShardName>()).Returns(Execution);
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ILoggerFactory>(), Arg.Any<ShardName>()).Returns(Execution);

            var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
                new ShardName("Trip"), factory, new EventFilterable());

            var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            store.Meter.Returns(new Meter("tests"));
            store.TimeProvider.Returns(TimeProvider.System);
            store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            store.RebuildErrors.Returns(new ErrorHandlingOptions());
            store.AllShards().Returns([shard]);

            var loader = caughtUpLoader();
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(loader);
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

            var database = Substitute.For<IEventDatabase>();
            database.Identifier.Returns("db1");
            database.DatabaseUri.Returns(new Uri("fake://db1"));
            database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                store, database, new NulloLogger(), new GlobalOnlyStubDetector(), new FakeProjectionGraph());
        }

        public RecordingExecution Execution { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    // Drains at once and records its disposal, which only the agent's DisposeAsync reaches.
    private sealed class RecordingExecution : ISubscriptionExecution
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingExecution(ShardName shardName)
        {
            ShardName = shardName;
        }

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

        public bool Disposed => _disposed.Task.IsCompleted;

        public Task WaitForDisposalAsync(TimeSpan timeout) => _disposed.Task.WaitAsync(timeout);

        public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;

        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
            => subscriptionAgent.MarkSuccessAsync(page.Ceiling);

        public Task HardStopAsync() => Task.CompletedTask;

        public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
        {
            executor = null;
            return false;
        }

        public Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
            CancellationToken cancellation) => Task.CompletedTask;

        public Task ProcessRangeAsync(EventRange range) => Task.CompletedTask;

        public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
        {
            caching = null;
            return false;
        }

        public ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    // No per-tenant partitioning, so the daemon skips the tenanted high-water machinery entirely.
    private sealed class GlobalOnlyStubDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
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
}
