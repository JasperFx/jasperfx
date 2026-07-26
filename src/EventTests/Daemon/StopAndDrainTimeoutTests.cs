using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using EventTests.Projections;
using JasperFx;
using JasperFx.Core;
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

// jasperfx#564: the daemon's graceful per-shard drain used to be bounded by a HARDCODED 5 seconds.
// StopAndDrainAsync is the good path — it awaits the in-flight page and flushes progression — so an
// in-flight page that needs longer than the bound got cancelled mid-flush, abandoning the progression
// write and surfacing as a ProgressionProgressOutOfOrderException on the next start. That matters most
// in exactly the deployments that need longer: thousands of (projection x tenant) shards draining
// inside a Kubernetes SIGTERM window. The bound is now DaemonSettings.StopAndDrainTimeout.
//
// These tests drive the REAL JasperFxAsyncDaemon (real SubscriptionAgents, substituted store/database)
// with an execution whose StopAndDrainAsync parks on a gate and records the token it was handed, so the
// drain's cancellation is observed directly instead of inferred from timing.
public class StopAndDrainTimeoutTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void the_default_is_still_five_seconds()
    {
        new DaemonSettings().StopAndDrainTimeout.ShouldBe(5.Seconds());
    }

    [Fact]
    public async Task a_raised_timeout_lets_a_slow_drain_finish_without_cancellation()
    {
        // THE issue's scenario: the in-flight page takes longer than the old hardcoded 5s to finish and
        // flush. With the bound raised, the drain must run to completion with its token intact.
        await using var harness = new DaemonHarness(settings => settings.StopAndDrainTimeout = 30.Seconds());

        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);

        var stop = harness.Daemon.StopAgentAsync("Trip:All");

        var drain = await harness.Execution.NextDrainAsync();

        // Well past the old 5s bound would be unreasonable in a test; what matters is that the drain
        // owns the decision to finish. Hold it long enough that an immediate/short bound would have
        // fired, then let it complete normally.
        await Task.Delay(750);
        drain.Token.IsCancellationRequested.ShouldBeFalse();
        drain.Release();

        await stop.WaitAsync(TestTimeout);

        harness.Execution.DrainCompletedUncancelled.ShouldBeTrue();
        harness.Daemon.CurrentAgents().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_lowered_timeout_bounds_the_drain_of_a_single_agent()
    {
        // The other direction — a host that would rather cut a wedged shard loose fast. 250ms also
        // proves the bound is really being read from settings: the old hardcoded 5s would have kept
        // StopAgentAsync parked far longer than the assertion below allows.
        await using var harness = new DaemonHarness(settings => settings.StopAndDrainTimeout = 250.Milliseconds());

        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var stop = harness.Daemon.StopAgentAsync("Trip:All");

        // The execution parks on the token, so only the configured bound can release it.
        await stop.WaitAsync(TestTimeout);
        stopwatch.Stop();

        harness.Execution.LastDrainToken!.Value.IsCancellationRequested.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeLessThan(3.Seconds());
        harness.Daemon.CurrentAgents().ShouldBeEmpty();
    }

    [Fact]
    public async Task the_stop_all_shutdown_path_honors_the_configured_timeout()
    {
        // StopAllAsync is the SIGTERM path from the issue, and it carried its own copy of the
        // hardcoded 5s.
        await using var harness = new DaemonHarness(settings => settings.StopAndDrainTimeout = 250.Milliseconds());

        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        await harness.Daemon.StopAllAsync().WaitAsync(TestTimeout);
        stopwatch.Stop();

        harness.Execution.LastDrainToken!.Value.IsCancellationRequested.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeLessThan(3.Seconds());
    }

    [Theory]
    [InlineData("zero")] // TimeSpan.Zero would cancel the drain IMMEDIATELY if it reached CancelAfter
    [InlineData("negative")] // and a negative value would throw out of CancelAfter outright
    [InlineData("infinite")] // Timeout.InfiniteTimeSpan is legal for CancelAfter and simply never fires
    public async Task a_non_positive_or_infinite_timeout_opts_out_of_the_separate_bound(string mode)
    {
        var timeout = mode switch
        {
            "zero" => TimeSpan.Zero,
            "negative" => TimeSpan.FromSeconds(-5),
            _ => Timeout.InfiniteTimeSpan
        };

        await using var harness = new DaemonHarness(settings => settings.StopAndDrainTimeout = timeout);

        await harness.Daemon.StartAgentAsync("Trip:All", CancellationToken.None);

        var stop = harness.Daemon.StopAgentAsync("Trip:All");
        var drain = await harness.Execution.NextDrainAsync();

        await Task.Delay(500);
        drain.Token.IsCancellationRequested.ShouldBeFalse();
        drain.Release();

        await stop.WaitAsync(TestTimeout);

        harness.Execution.DrainCompletedUncancelled.ShouldBeTrue();
    }

    // Real daemon + real SubscriptionAgent over a substituted store/database, with the gated
    // execution below standing in for the in-flight page + progression flush.
    private sealed class DaemonHarness : IAsyncDisposable
    {
        public DaemonHarness(Action<DaemonSettings> configureSettings)
        {
            Execution = new GatedDrainExecution(new ShardName("Trip"));

            var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ILogger>(), Arg.Any<ShardName>()).Returns(Execution);
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ILoggerFactory>(), Arg.Any<ShardName>()).Returns(Execution);

            var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
                new ShardName("Trip"), factory, new EventFilterable());

            Store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            Store.Meter.Returns(new Meter("tests"));
            Store.TimeProvider.Returns(TimeProvider.System);
            Store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            Store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            Store.RebuildErrors.Returns(new ErrorHandlingOptions());
            Store.AllShards().Returns([shard]);

            // An empty, already-caught-up page keeps the started agent healthy with no real storage
            // behind it — the drain is what these tests are about.
            var loader = Substitute.For<IEventLoader>();
            loader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var request = callInfo.Arg<EventRequest>();
                    var page = new EventPage(request.Floor);
                    page.CalculateCeiling(request.BatchSize, request.HighWater);
                    return Task.FromResult(page);
                });

            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(loader);
            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

            Database = Substitute.For<IEventDatabase>();
            Database.Identifier.Returns("db1");
            Database.DatabaseUri.Returns(new Uri("fake://db1"));
            Database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

            Projections = new FakeProjectionGraph();
            configureSettings(Projections);

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                Store, Database, new NulloLogger(), new GlobalOnlyStubDetector(), Projections);
        }

        public IEventStore<FakeOperations, FakeSession> Store { get; }
        public IEventDatabase Database { get; }
        public GatedDrainExecution Execution { get; }
        public ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession> Projections { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            // Don't let a still-parked drain hold up teardown of the tests that never released it
            Execution.ReleaseAll();
            Projections.StopAndDrainTimeout = 100.Milliseconds();
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    // StopAndDrainAsync parks until either the test releases it or its token is cancelled, and records
    // the token it was handed so the bound can be observed directly.
    private sealed class GatedDrainExecution : ISubscriptionExecution
    {
        private readonly Channel<DrainEntry> _drains = Channel.CreateUnbounded<DrainEntry>();
        private readonly List<DrainEntry> _all = new();

        public GatedDrainExecution(ShardName shardName)
        {
            ShardName = shardName;
        }

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

        public CancellationToken? LastDrainToken { get; private set; }

        public bool DrainCompletedUncancelled { get; private set; }

        public async Task<DrainEntry> NextDrainAsync()
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            return await _drains.Reader.ReadAsync(timeout.Token);
        }

        public void ReleaseAll()
        {
            lock (_all)
            {
                foreach (var entry in _all)
                {
                    entry.Release();
                }
            }
        }

        public async Task StopAndDrainAsync(CancellationToken token)
        {
            LastDrainToken = token;

            var entry = new DrainEntry(token);
            lock (_all)
            {
                _all.Add(entry);
            }

            _drains.Writer.TryWrite(entry);

            // Stand in for finishing the in-flight page and flushing progression: either the test
            // lets it complete, or the configured bound cancels it out from under us.
            await entry.WaitAsync();

            if (!token.IsCancellationRequested)
            {
                DrainCompletedUncancelled = true;
            }
        }

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class DrainEntry
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DrainEntry(CancellationToken token)
        {
            Token = token;
        }

        public CancellationToken Token { get; }

        public void Release() => _gate.TrySetResult();

        public async Task WaitAsync()
        {
            // Completes on the gate OR on the drain token, whichever comes first, without throwing —
            // the caller inspects the token to tell the two apart.
            var cancelled = new TaskCompletionSource();
            await using var registration = Token.Register(() => cancelled.TrySetResult());
            await Task.WhenAny(_gate.Task, cancelled.Task);
        }
    }

    // No per-tenant partitioning: SupportsTenantPartitioning stays false, so the daemon skips the
    // tenanted high-water machinery entirely.
    private sealed class GlobalOnlyStubDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    // Minimal concrete ProjectionGraph — on these paths the daemon consumes it only as DaemonSettings.
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
