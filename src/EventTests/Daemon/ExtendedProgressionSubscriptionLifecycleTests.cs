using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

/// <summary>
/// jasperfx#621 — the ShardStateTracker is per-database and SHARED, so subscribing an
/// ExtendedProgressionWriter in the daemon constructor meant every daemon ever built for a database
/// added another writer to the same publication stream, each renting its own connection and issuing
/// the same UPDATE against the same rows. Building a daemon is a documented way to *read* projection
/// state (BuildProjectionDaemonAsync returns a fresh instance per call, no caching), and a read must
/// not acquire a background write loop as an invisible side effect.
/// </summary>
public class ExtendedProgressionSubscriptionLifecycleTests : IDisposable
{
    private readonly ShardStateTracker theTracker = new(new NulloLogger());
    private readonly RecordingDatabase theDatabase;

    public ExtendedProgressionSubscriptionLifecycleTests()
    {
        theDatabase = new RecordingDatabase(theTracker);
    }

    private readonly List<JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>>
        theDaemons = [];

    public void Dispose()
    {
        foreach (var daemon in theDaemons) daemon.Dispose();
    }

    private JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> buildDaemon()
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.Meter.Returns(new Meter("tests"));
        store.TimeProvider.Returns(TimeProvider.System);
        store.ExtendedProgressionEnabled.Returns(true);
        store.AutoCreateSchemaObjects.Returns(AutoCreate.None);

        var daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
            store, theDatabase, new NulloLogger(), new StubDetector(), new FakeGraph());

        theDaemons.Add(daemon);
        return daemon;
    }

    // jasperfx#622: only status transitions are persisted by default, so the probe publication that
    // proves "this daemon is writing" has to be one
    private static ShardState startedState(string shardName) =>
        new(shardName, 5)
        {
            Action = ShardAction.Started, AgentStatus = "Running", LastHeartbeat = DateTimeOffset.UtcNow
        };

    // Publications are delivered asynchronously through the tracker's block, so both the positive and
    // the negative assertion have to be given the same chance to happen
    private async Task<int> writeCountAfterPublishing(ShardState state, int expected)
    {
        await theTracker.PublishAsync(state);

        for (var i = 0; i < 100 && theDatabase.Writes < expected; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        if (expected == 0)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        return theDatabase.Writes;
    }

    [Fact]
    public async Task an_unstarted_daemon_does_not_write_extended_progression()
    {
        // The reported shape: an EventProgressionPoller building a daemon per node-owned database
        // every 15 seconds purely to read CurrentAgents()
        var daemon = buildDaemon();

        (await writeCountAfterPublishing(startedState("Trip:All"), 0)).ShouldBe(0);
    }

    [Fact]
    public async Task many_unstarted_daemons_on_one_shared_tracker_still_write_nothing()
    {
        for (var i = 0; i < 5; i++)
        {
            buildDaemon();
        }

        (await writeCountAfterPublishing(startedState("Trip:All"), 0)).ShouldBe(0);
    }

    [Fact]
    public async Task a_started_daemon_does_write_extended_progression()
    {
        var daemon = buildDaemon();
        await daemon.StartHighWaterDetectionAsync();

        (await writeCountAfterPublishing(startedState("Trip:All"), 1)).ShouldBe(1);
    }

    [Fact]
    public async Task only_the_started_daemon_writes_when_readers_share_the_tracker()
    {
        // One owner plus three ad-hoc readers on the same database: exactly one write per publication,
        // not four. This is the multiplication behind the pg_stat_activity self-blocking in marten#5167.
        var owner = buildDaemon();
        buildDaemon();
        buildDaemon();
        buildDaemon();

        await owner.StartHighWaterDetectionAsync();

        (await writeCountAfterPublishing(startedState("Trip:All"), 1)).ShouldBe(1);
    }

    [Fact]
    public async Task starting_twice_does_not_stack_a_second_subscription()
    {
        var daemon = buildDaemon();
        await daemon.StartHighWaterDetectionAsync();
        await daemon.StartHighWaterDetectionAsync();

        (await writeCountAfterPublishing(startedState("Trip:All"), 1)).ShouldBe(1);
    }

    [Fact]
    public async Task a_stopped_daemon_stops_writing()
    {
        var daemon = buildDaemon();
        await daemon.StartHighWaterDetectionAsync();
        (await writeCountAfterPublishing(startedState("Trip:All"), 1)).ShouldBe(1);

        await daemon.StopAllAsync();

        (await writeCountAfterPublishing(startedState("Trip:All"), 2)).ShouldBe(1);
    }

    [Fact]
    public async Task a_restarted_daemon_resumes_writing()
    {
        // jasperfx#557's guarantee, preserved: the drained writer is rebuilt on the next start rather
        // than eagerly at the end of StopAllAsync
        var daemon = buildDaemon();
        await daemon.StartHighWaterDetectionAsync();
        await daemon.StopAllAsync();
        await daemon.StartHighWaterDetectionAsync();

        (await writeCountAfterPublishing(startedState("Trip:All"), 1)).ShouldBe(1);
    }

    [Fact]
    public void disposing_a_never_started_daemon_is_clean()
    {
        var daemon = buildDaemon();
        Should.NotThrow(daemon.Dispose);
        Should.NotThrow(daemon.Dispose);
    }

    private sealed class RecordingDatabase : IEventDatabase
    {
        private int _writes;

        public RecordingDatabase(ShardStateTracker tracker) => Tracker = tracker;

        public int Writes => Volatile.Read(ref _writes);

        public Task WriteExtendedProgressionAsync(IReadOnlyList<ShardState> states, CancellationToken token = default)
        {
            Interlocked.Increment(ref _writes);
            return Task.CompletedTask;
        }

        public Task WriteExtendedProgressionAsync(ShardState state, CancellationToken token = default)
        {
            Interlocked.Increment(ref _writes);
            return Task.CompletedTask;
        }

        public ShardStateTracker Tracker { get; }
        public string Identifier => "db1";
        public Uri DatabaseUri { get; } = new("fake://db1");
        public string StorageIdentifier => "db1";

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => Task.CompletedTask;

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token) => Task.CompletedTask;
        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => Task.CompletedTask;

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => Task.FromResult(0L);

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => Task.FromResult<long?>(null);

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token) => Task.FromResult(0L);

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ShardState>>([]);
    }

    private sealed class StubDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

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
