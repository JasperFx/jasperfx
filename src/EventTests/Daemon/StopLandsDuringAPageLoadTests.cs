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

// jasperfx#1231: CatchUpAsync stops every running agent first. An agent stopped while it was loading a page used to
// post that page once the load came back, into an execution the stop had already completed: "The channel has been
// closed", reported as the shard's failure, which failed a catch-up that had worked. Stopping an agent now waits for
// the command it is in the middle of, and a load that comes back after the stop drops its page.
//
// The continuous agent's first load is held open until the stop cancels it, then hands back a page anyway, as a
// load that finished just as the stop landed does. The execution counts every page posted to it after the agent
// was told to stop, which is what a real execution answers with "The channel has been closed".
public class StopLandsDuringAPageLoadTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task a_page_that_loads_as_the_agent_stops_is_not_posted()
    {
        await using var harness = new Harness(LateLoad.AtTheStop);

        await harness.Daemon.StartAllAsync();
        await harness.Loader.InFlightAsync();
        var agent = harness.Daemon.CurrentAgents().Single();

        await harness.Daemon.StopAllAsync();

        harness.Loader.HeldLoadReturned.ShouldBeTrue();
        harness.Execution.PostsAfterStop.ShouldBe(0);
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public async Task stopping_waits_for_a_load_that_comes_back_after_the_stop()
    {
        await using var harness = new Harness(LateLoad.AfterTheStop);

        await harness.Daemon.StartAllAsync();
        await harness.Loader.InFlightAsync();
        var agent = harness.Daemon.CurrentAgents().Single();

        await harness.Daemon.StopAllAsync();

        // The stop did not return until the agent had the page back and had dropped it
        harness.Loader.HeldLoadReturned.ShouldBeTrue();
        harness.Execution.PostsAfterStop.ShouldBe(0);
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public async Task catch_up_after_start_all_does_not_fail_on_a_load_the_stop_interrupted()
    {
        await using var harness = new Harness(LateLoad.AtTheStop);

        await harness.Daemon.StartAllAsync();
        await harness.Loader.InFlightAsync();

        await Should.NotThrowAsync(() => harness.Daemon.CatchUpAsync(CancellationToken.None));

        harness.Execution.PostsAfterStop.ShouldBe(0);
        harness.Execution.CaughtUpThrough.ShouldBe(10);
    }

    public enum LateLoad
    {
        // The page comes back from inside the stop's cancellation
        AtTheStop,

        // The page comes back a little after the stop's cancellation, on another thread
        AfterTheStop
    }

    private sealed class HeldLoader : IEventLoader
    {
        private readonly LateLoad _late;
        private readonly TaskCompletionSource _inFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loads;
        private volatile bool _returned;

        public HeldLoader(LateLoad late) => _late = late;

        public bool HeldLoadReturned => _returned;

        public Task InFlightAsync() => _inFlight.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            var page = new EventPage(request.Floor);
            page.CalculateCeiling(request.BatchSize, request.HighWater);

            // Only the continuous agent's first load is held; the catch-up agent's loads answer at once
            if (Interlocked.Increment(ref _loads) > 1) return Task.FromResult(page);

            // Deliberately without RunContinuationsAsynchronously: at the stop, the agent carries on with the page
            // from inside the cancellation, before the stop gets any further
            var loaded = new TaskCompletionSource<EventPage>();
            token.Register(() =>
            {
                if (_late == LateLoad.AtTheStop)
                {
                    hand(loaded, page);
                    return;
                }

                _ = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    hand(loaded, page);
                });
            });

            _inFlight.TrySetResult();
            return loaded.Task;
        }

        private void hand(TaskCompletionSource<EventPage> loaded, EventPage page)
        {
            _returned = true;
            loaded.TrySetResult(page);
        }
    }

    private sealed class RecordingExecution : ISubscriptionExecution
    {
        private int _postsAfterStop;
        private long _caughtUpThrough;

        public RecordingExecution(ShardName name) => ShardName = name;

        public int PostsAfterStop => Volatile.Read(ref _postsAfterStop);

        public long CaughtUpThrough => Interlocked.Read(ref _caughtUpThrough);

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; }

        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
        {
            if (((SubscriptionAgent)subscriptionAgent).CancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _postsAfterStop);
                throw new ChannelClosedException();
            }

            return subscriptionAgent.MarkSuccessAsync(page.Ceiling);
        }

        public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;

        public Task HardStopAsync() => Task.CompletedTask;

        public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
        {
            executor = null;
            return false;
        }

        public Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
            CancellationToken cancellation)
        {
            Interlocked.Exchange(ref _caughtUpThrough, events.Ceiling);
            return Task.CompletedTask;
        }

        public Task ProcessRangeAsync(EventRange range) => Task.CompletedTask;

        public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
        {
            caching = null;
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Detector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");
        public bool SupportsTenantPartitioning => false;

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics { CurrentMark = 10, HighestSequence = 10, LastMark = 0 });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => throw new NotSupportedException();

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FakeProjectionGraph :
        ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeProjectionGraph() : base(Substitute.For<IEventRegistry>(), "tests") { }
        protected override void onAddProjection(object projection) { }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(LateLoad late)
        {
            Loader = new HeldLoader(late);

            // One execution for every agent of the shard: the continuous one and the catch-up one
            Execution = new RecordingExecution(new ShardName("Trip"));
            var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                    Arg.Any<ILogger>(), Arg.Any<ShardName>())
                .Returns(Execution);

            var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
                new ShardName("Trip"), factory, new EventFilterable());

            var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            store.Meter.Returns(new Meter("tests"));
            store.TimeProvider.Returns(TimeProvider.System);
            store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            store.RebuildErrors.Returns(new ErrorHandlingOptions());
            store.AllShards().Returns([shard]);
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(Loader);
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(Loader);

            var database = Substitute.For<IEventDatabase>();
            database.Identifier.Returns("db1");
            database.DatabaseUri.Returns(new Uri("fake://db1"));
            database.AllProjectionProgress(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ShardState>>(Array.Empty<ShardState>()));
            database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                store, database, new NulloLogger(), new Detector(), new FakeProjectionGraph());
        }

        public HeldLoader Loader { get; }
        public RecordingExecution Execution { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }
}
