using System.Collections.Concurrent;
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

// jasperfx#1225: with extended progression on, StopAllAsync() followed straight away by Dispose() left the
// shard's progression row at agent_status = Running for good. The agent publishes its Stopped state through
// ShardStateTracker.PublishAsync, which only queues it on the tracker's block, and StopAllAsync then
// unsubscribed the ExtendedProgressionWriter before the tracker had handed the queued state to it. The writer
// never saw Stopped, so the drain that #557 added had nothing to write.
//
// These tests drive the real JasperFxAsyncDaemon and real SubscriptionAgents over a database that records
// every extended progression write, and read the "row" right after the stop returns, with no waiting.
public class StopSavesStoppedStatusTests : IAsyncDisposable
{
    private const string TheShard = "Trip:All";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly ShardStateTracker theTracker = new(new NulloLogger());
    private readonly RecordingDatabase theDatabase;
    private readonly JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> theDaemon;

    public StopSavesStoppedStatusTests()
    {
        theDatabase = new RecordingDatabase(theTracker);

        var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();
        factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
            Arg.Any<ILogger>(), Arg.Any<ShardName>()).Returns(_ => new QuietExecution(new ShardName("Trip")));
        factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
            Arg.Any<ILoggerFactory>(), Arg.Any<ShardName>()).Returns(_ => new QuietExecution(new ShardName("Trip")));

        var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
            new ShardName("Trip"), factory, new EventFilterable());

        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.Meter.Returns(new Meter("tests"));
        store.TimeProvider.Returns(TimeProvider.System);
        store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
        store.ContinuousErrors.Returns(new ErrorHandlingOptions());
        store.RebuildErrors.Returns(new ErrorHandlingOptions());
        store.ExtendedProgressionEnabled.Returns(true);
        store.AllShards().Returns([shard]);

        // An empty, caught-up page: the agent stays healthy with no storage behind it
        var loader = Substitute.For<IEventLoader>();
        loader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<EventRequest>();
                var page = new EventPage(request.Floor);
                page.CalculateCeiling(request.BatchSize, request.HighWater);
                return Task.FromResult(page);
            });

        store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
            Arg.Any<AsyncOptions>()).Returns(loader);
        store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
            Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

        theDaemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
            store, theDatabase, new NulloLogger(), new CaughtUpDetector(), new FakeGraph());
    }

    public ValueTask DisposeAsync()
    {
        theDaemon.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task startTheShardAsync()
        => theDaemon.StartAgentAsync(TheShard, CancellationToken.None).WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task stop_all_then_an_immediate_dispose_leaves_the_row_stopped()
    {
        await startTheShardAsync();

        await theDaemon.StopAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        theDaemon.Dispose();

        // Read straight away: the #557 promise is that the write has happened before StopAllAsync returns
        theDatabase.StatusOf(TheShard).ShouldBe("Stopped");
    }

    [Fact]
    public async Task stop_all_waits_for_the_tracker_to_hand_the_stopped_state_to_the_writer()
    {
        // Holds up the tracker's delivery of the Stopped state, the way a busy tracker or a slow observer
        // ahead of the writer does. Subscribed before the daemon starts, so it is ahead of the writer.
        using var gate = new StoppedStateGate();
        using var subscription = theTracker.Subscribe(gate);

        await startTheShardAsync();

        var stop = theDaemon.StopAllAsync();

        await gate.Holding.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        stop.IsCompleted.ShouldBeFalse("StopAllAsync returned while its Stopped state was still queued on the tracker");

        gate.Release();
        await stop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        theDaemon.Dispose();

        theDatabase.StatusOf(TheShard).ShouldBe("Stopped");
    }

    [Fact]
    public async Task stop_agent_then_an_immediate_dispose_leaves_the_row_stopped()
    {
        await startTheShardAsync();

        await theDaemon.StopAgentAsync(TheShard).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        theDaemon.Dispose();

        theDatabase.StatusOf(TheShard).ShouldBe("Stopped");
    }

    [Fact]
    public async Task stop_agent_waits_for_the_tracker_to_hand_the_stopped_state_to_the_writer()
    {
        using var gate = new StoppedStateGate();
        using var subscription = theTracker.Subscribe(gate);

        await startTheShardAsync();

        var stop = theDaemon.StopAgentAsync(TheShard);

        await gate.Holding.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        stop.IsCompleted.ShouldBeFalse("StopAgentAsync returned while its Stopped state was still queued on the tracker");

        gate.Release();
        await stop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        theDaemon.Dispose();

        theDatabase.StatusOf(TheShard).ShouldBe("Stopped");
    }

    [Fact]
    public async Task starting_and_stopping_straight_away_still_ends_stopped()
    {
        // The Started and Stopped writes queue one behind the other; the last one written has to be Stopped
        await startTheShardAsync();
        await theDaemon.StopAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        theDaemon.Dispose();

        theDatabase.StatusesWritten(TheShard).ShouldBe(["Running", "Stopped"]);
    }

    [Fact]
    public async Task starting_writes_running_promptly()
    {
        // The opposite direction. Starting does not wait for the write, but nothing holds it back either: it is
        // a status transition, so the writer flushes it as soon as the tracker delivers it rather than on a
        // heartbeat interval (which is off by default).
        await startTheShardAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (theDatabase.StatusOf(TheShard) != "Running" && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        theDatabase.StatusOf(TheShard).ShouldBe("Running");
    }

    [Fact]
    public async Task a_restarted_shard_is_running_again_after_a_stop()
    {
        await startTheShardAsync();
        await theDaemon.StopAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        theDatabase.StatusOf(TheShard).ShouldBe("Stopped");

        await startTheShardAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (theDatabase.StatusOf(TheShard) != "Running" && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        theDatabase.StatusOf(TheShard).ShouldBe("Running");
    }

    // Blocks the tracker's publication loop on the first Stopped state until released
    private sealed class StoppedStateGate : IObserver<ShardState>, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Holding => _holding.Task;

        public void Release() => _release.Set();

        public void OnNext(ShardState value)
        {
            if (value.Action != ShardAction.Stopped || value.ShardName != TheShard) return;

            _holding.TrySetResult();
            _release.Wait(TestTimeout);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    // Stands in for a store's progression table: the agent_status each write left on each shard's row
    private sealed class RecordingDatabase : IEventDatabase
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _statuses = new();

        public RecordingDatabase(ShardStateTracker tracker) => Tracker = tracker;

        public string? StatusOf(string shardName)
            => _statuses.TryGetValue(shardName, out var written) ? written.LastOrDefault() : null;

        public string[] StatusesWritten(string shardName)
            => _statuses.TryGetValue(shardName, out var written) ? written.ToArray() : [];

        public Task WriteExtendedProgressionAsync(IReadOnlyList<ShardState> states, CancellationToken token = default)
        {
            foreach (var state in states)
            {
                record(state);
            }

            return Task.CompletedTask;
        }

        public Task WriteExtendedProgressionAsync(ShardState state, CancellationToken token = default)
        {
            record(state);
            return Task.CompletedTask;
        }

        private void record(ShardState state)
        {
            if (state.AgentStatus == null) return;
            _statuses.GetOrAdd(state.ShardName, _ => new ConcurrentQueue<string>()).Enqueue(state.AgentStatus);
        }

        public ShardStateTracker Tracker { get; }
        public string Identifier => "db1";
        public Uri DatabaseUri { get; } = new("fake://db1");
        public string StorageIdentifier => "db1";

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => Task.CompletedTask;

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token) => Task.CompletedTask;
        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => Task.CompletedTask;

        // A shard that has committed before, so its row exists and a status write has somewhere to land
        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => Task.FromResult(5L);

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => Task.FromResult<long?>(null);

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token) => Task.FromResult(5L);

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ShardState>>([]);
    }

    private sealed class QuietExecution : ISubscriptionExecution
    {
        public QuietExecution(ShardName shardName)
        {
            ShardName = shardName;
        }

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaughtUpDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics { CurrentMark = 5, HighestSequence = 5 });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    private sealed class FakeGraph : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }
}
