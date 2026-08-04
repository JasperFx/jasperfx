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
/// jasperfx#622 — the periodic per-shard extended progression heartbeat had no reader anywhere and
/// cost one pooled connection + one transaction per database per node every 5 seconds, with no
/// configuration path at all (the interval was private on the daemon and no DaemonSettings knob
/// reached it). It is off by default now, and DaemonSettings.ExtendedProgressionHeartbeatInterval is
/// the compatibility hatch.
/// </summary>
public class ExtendedProgressionHeartbeatIntervalTests : IDisposable
{
    private readonly ShardStateTracker theTracker = new(new NulloLogger());
    private readonly RecordingDatabase theDatabase;

    private readonly List<JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>>
        theDaemons = [];

    public ExtendedProgressionHeartbeatIntervalTests()
    {
        theDatabase = new RecordingDatabase(theTracker);
    }

    public void Dispose()
    {
        foreach (var daemon in theDaemons) daemon.Dispose();
    }

    private async Task<JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>>
        startedDaemon(TimeSpan? heartbeatInterval)
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.Meter.Returns(new Meter("tests"));
        store.TimeProvider.Returns(TimeProvider.System);
        store.ExtendedProgressionEnabled.Returns(true);
        store.AutoCreateSchemaObjects.Returns(AutoCreate.None);

        var graph = new FakeGraph { ExtendedProgressionHeartbeatInterval = heartbeatInterval };

        var daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
            store, theDatabase, new NulloLogger(), new StubDetector(), graph);

        theDaemons.Add(daemon);
        await daemon.StartHighWaterDetectionAsync();
        return daemon;
    }

    private static ShardState heartbeat() => new("Trip:All", 5)
    {
        Action = ShardAction.Updated, AgentStatus = "Running", LastHeartbeat = DateTimeOffset.UtcNow
    };

    private static ShardState transition() => new("Trip:All", 5)
    {
        Action = ShardAction.Paused, AgentStatus = "Paused", PauseReason = "boom"
    };

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
    public async Task no_periodic_heartbeat_write_by_default()
    {
        await startedDaemon(null);

        (await writeCountAfterPublishing(heartbeat(), 0)).ShouldBe(0);
    }

    [Fact]
    public async Task transitions_are_still_persisted_by_default()
    {
        await startedDaemon(null);

        (await writeCountAfterPublishing(transition(), 1)).ShouldBe(1);
    }

    [Fact]
    public async Task a_configured_interval_restores_the_periodic_beat()
    {
        await startedDaemon(TimeSpan.FromSeconds(5));

        (await writeCountAfterPublishing(heartbeat(), 1)).ShouldBe(1);
    }

    [Fact]
    public async Task a_non_positive_configured_interval_is_off()
    {
        await startedDaemon(TimeSpan.Zero);

        (await writeCountAfterPublishing(heartbeat(), 0)).ShouldBe(0);
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
