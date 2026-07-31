using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
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

// jasperfx#594: the blue/green side-effect gate (jasperfx#480) runs a bounded, side-effect-suppressed
// warm-up replay INSIDE the agent start path, and that bound used to be a HARDCODED 5 minutes. Two
// things went wrong at scale (512 tenant databases, warm-ups measured at 27s p50 / 82s p95 / 288.5s max
// against the 300s ceiling):
//
//   1. The ceiling could not be raised, so start failures were the next sample rather than a tail risk.
//   2. A timeout was treated as a FAILURE even when the replay had actually finished — a shard was seen
//      failing its start three times on this timeout while its progression sat at exactly the prior
//      version's mark, each retry re-running a warm-up that had nothing left to do.
//
// These tests drive the REAL JasperFxAsyncDaemon over a substituted store/database, with an execution
// that never completes its rebuild so the gate's timeout fires deterministically.
public class SideEffectGateTimeoutTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private const long PriorMark = 100;

    [Fact]
    public void the_default_is_still_five_minutes()
    {
        new DaemonSettings().SideEffectGateTimeout.ShouldBe(5.Minutes());
    }

    [Fact]
    public async Task a_timeout_that_reached_the_prior_mark_is_treated_as_warmed_up()
    {
        // THE issue's scenario. The warm-up replay wins the race against nothing — it never signals
        // completion, so the gate times out — but the PERSISTED progression has reached the prior
        // version's mark. That is a warm-up that succeeded and a wait that expired, and the shard must
        // go on to start continuous execution rather than be failed and retried forever.
        await using var harness = new DaemonHarness(
            settings => settings.SideEffectGateTimeout = 250.Milliseconds(),
            progressReads: [0, PriorMark]);

        await harness.Daemon.StartAgentAsync("Trip:V2:All", CancellationToken.None);

        // The gate returned true, so the continuous agent was built and started.
        harness.Daemon.CurrentAgents().ShouldContain(x => x.Name.Identity == "Trip:V2:All");
    }

    [Fact]
    public async Task a_timeout_that_did_not_reach_the_prior_mark_pauses_the_shard()
    {
        // The genuine failure. Side effects must NOT be enabled over history the prior version already
        // covered, so no continuous agent starts — but the shard is published as Paused rather than left
        // silently stopped, so it is visible to a supervisor and resumes from its persisted floor.
        var paused = new List<ShardState>();

        await using var harness = new DaemonHarness(
            settings => settings.SideEffectGateTimeout = 250.Milliseconds(),
            progressReads: [0, PriorMark - 40],
            onShardState: state =>
            {
                if (state.Action == ShardAction.Paused) paused.Add(state);
            });

        await harness.Daemon.StartAgentAsync("Trip:V2:All", CancellationToken.None);

        harness.Daemon.CurrentAgents().ShouldNotContain(x => x.Name.Identity == "Trip:V2:All");

        var state = paused.ShouldHaveSingleItem();
        state.ShardName.ShouldBe("Trip:V2:All");
        state.Sequence.ShouldBe(PriorMark - 40);
        state.PauseReason.ShouldContain("side-effect gate warm-up timed out");
    }

    [Theory]
    [InlineData("zero")] // TimeSpan.Zero would cancel the replay IMMEDIATELY if it reached the timeout
    [InlineData("negative")] // and a negative value is not a legal CancellationTokenSource delay
    [InlineData("infinite")] // Timeout.InfiniteTimeSpan is the explicit "no separate bound" spelling
    public async Task a_non_positive_or_infinite_timeout_opts_out_of_the_separate_bound(string mode)
    {
        var timeout = mode switch
        {
            "zero" => TimeSpan.Zero,
            "negative" => TimeSpan.FromSeconds(-5),
            _ => Timeout.InfiniteTimeSpan
        };

        await using var harness = new DaemonHarness(
            settings => settings.SideEffectGateTimeout = timeout,
            progressReads: [0, PriorMark]);

        var start = harness.Daemon.StartAgentAsync("Trip:V2:All", CancellationToken.None);

        // Nothing will ever complete this warm-up, so with the bound opted out the start must still be
        // parked. That is the whole assertion: a zero or negative value reaching the replay's
        // CancellationTokenSource/TimeoutAfterAsync would have fired essentially immediately, and the
        // real 5-minute default would too if the setting were not being read at all.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        start.IsCompleted.ShouldBeFalse();

        // The start stays parked for the life of the test; teardown stops the daemon out from under it.
        // Observe it so a post-teardown fault never surfaces as an unobserved task exception.
        _ = start.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }

    // Real daemon + real SubscriptionAgent over a substituted store/database. The shard opts into the
    // gate and is at V2, and the database reports a V1 row at PriorMark, so the gate always triggers.
    private sealed class DaemonHarness : IAsyncDisposable
    {
        public DaemonHarness(
            Action<DaemonSettings> configureSettings,
            long[] progressReads,
            Action<ShardState>? onShardState = null)
        {
            var shardName = new ShardName("Trip", ShardName.All, 2, null);
            Execution = new NeverCompletingRebuildExecution(shardName);

            var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ILogger>(), Arg.Any<ShardName>()).Returns(Execution);
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ILoggerFactory>(), Arg.Any<ShardName>()).Returns(Execution);

            var options = new AsyncOptions { GateSideEffectsBehindPriorVersion = true };
            var shard = new AsyncShard<FakeOperations, FakeSession>(options, ShardRole.Projection,
                shardName, factory, new EventFilterable());

            Store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            Store.Meter.Returns(new Meter("tests"));
            Store.TimeProvider.Returns(TimeProvider.System);
            Store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            Store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            Store.RebuildErrors.Returns(new ErrorHandlingOptions());
            Store.AllShards().Returns([shard]);

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

            var tracker = new ShardStateTracker(new NulloLogger());
            if (onShardState != null)
            {
                tracker.Subscribe(new ShardStateObserver(onShardState));
            }

            Database.Tracker.Returns(tracker);

            // The gate reads persisted progression twice on the timeout path: once up front to decide
            // whether it triggers, then again to judge whether the replay actually finished.
            var reads = new Queue<long>(progressReads);
            Database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(reads.Count > 0 ? reads.Dequeue() : 0L));

            // The prior version's row, which is what makes the gate trigger at all.
            Database.AllProjectionProgress(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ShardState>>(
                    [new ShardState(new ShardName("Trip", ShardName.All, 1, null), PriorMark)]));

            Projections = new FakeProjectionGraph();
            configureSettings(Projections);

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                Store, Database, new NulloLogger(), new GlobalOnlyStubDetector(), Projections);
        }

        public IEventStore<FakeOperations, FakeSession> Store { get; }
        public IEventDatabase Database { get; }
        public NeverCompletingRebuildExecution Execution { get; }
        public ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession> Projections { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            Execution.CompleteRebuild();
            Projections.StopAndDrainTimeout = 100.Milliseconds();
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    // The warm-up replay parks forever unless the test releases it, so the gate's timeout — and only the
    // gate's timeout — decides when the start path moves on.
    private sealed class NeverCompletingRebuildExecution : ISubscriptionExecution
    {
        private readonly TaskCompletionSource _rebuild = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NeverCompletingRebuildExecution(ShardName shardName)
        {
            ShardName = shardName;
        }

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

        public void CompleteRebuild() => _rebuild.TrySetResult();


        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
        {
            // In Rebuild mode swallow the page so the agent never reports its way to completion; in
            // Continuous mode behave normally so a started agent looks healthy.
            return Mode == ShardExecutionMode.Rebuild
                ? ValueTask.CompletedTask
                : subscriptionAgent.MarkSuccessAsync(page.Ceiling);
        }

        public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;

        public Task HardStopAsync() => Task.CompletedTask;

        public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
        {
            // Force the non-optimized path, which is the one bounded by TimeoutAfterAsync.
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

    private sealed class ShardStateObserver : IObserver<ShardState>
    {
        private readonly Action<ShardState> _onNext;

        public ShardStateObserver(Action<ShardState> onNext) => _onNext = onNext;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value) => _onNext(value);
    }

    private sealed class GlobalOnlyStubDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics { CurrentMark = PriorMark, HighestSequence = PriorMark });

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
