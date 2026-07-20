using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests.Daemon;

public class ExtendedProgressionWriterTests
{
    private readonly IEventStore theStore = Substitute.For<IEventStore>();
    private readonly RecordingEventDatabase theDatabase = new();
    private readonly FakeTimeProvider theTime = new();
    private readonly ExtendedProgressionWriter theWriter;

    public ExtendedProgressionWriterTests()
    {
        theStore.ExtendedProgressionEnabled.Returns(true);
        theWriter = new ExtendedProgressionWriter(theStore, theDatabase, theTime, NullLogger.Instance);
    }

    private static ShardState transition(ShardAction action, string status, string? pauseReason = null,
        string shardName = "Counters:All")
    {
        return new ShardState(shardName, 42)
        {
            Action = action,
            AgentStatus = status,
            PauseReason = pauseReason,
            LastHeartbeat = DateTimeOffset.UtcNow
        };
    }

    private static ShardState heartbeat(string shardName = "Counters:All")
    {
        return new ShardState(shardName, 42)
        {
            Action = ShardAction.Updated,
            AgentStatus = "Running",
            LastHeartbeat = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public async Task writes_status_transitions_through_the_database()
    {
        theWriter.OnNext(transition(ShardAction.Started, "Running"));
        theWriter.OnNext(transition(ShardAction.Paused, "Paused", "boom"));
        theWriter.OnNext(transition(ShardAction.Stopped, "Stopped"));

        var writes = await theDatabase.WaitForWrites(3);

        writes[0].AgentStatus.ShouldBe("Running");
        writes[1].AgentStatus.ShouldBe("Paused");
        writes[1].PauseReason.ShouldBe("boom");
        writes[2].AgentStatus.ShouldBe("Stopped");
    }

    [Fact]
    public async Task no_writes_at_all_when_the_store_has_not_opted_in()
    {
        theStore.ExtendedProgressionEnabled.Returns(false);

        theWriter.OnNext(transition(ShardAction.Started, "Running"));
        theWriter.OnNext(heartbeat());
        theWriter.OnNext(transition(ShardAction.Stopped, "Stopped"));

        await theDatabase.AssertNoWrites();
    }

    [Fact]
    public async Task skips_high_water_mark_and_all_projections_states()
    {
        theWriter.OnNext(new ShardState(ShardState.HighWaterMark, 100)
        {
            AgentStatus = "Running", LastHeartbeat = DateTimeOffset.UtcNow
        });
        theWriter.OnNext(new ShardState(ShardState.AllProjections, 100)
        {
            AgentStatus = "Running", LastHeartbeat = DateTimeOffset.UtcNow
        });

        await theDatabase.AssertNoWrites();
    }

    [Fact]
    public async Task skips_plain_progress_publications_with_no_agent_telemetry()
    {
        // e.g. the RangeCompleted publication from a rebuild
        theWriter.OnNext(new ShardState("Counters:All", 42));

        await theDatabase.AssertNoWrites();
    }

    [Fact]
    public async Task throttles_heartbeat_writes_per_shard_but_never_transitions()
    {
        theWriter.OnNext(heartbeat());
        theWriter.OnNext(heartbeat()); // same instant, throttled
        theWriter.OnNext(heartbeat("Others:All")); // different shard, its own budget

        var writes = await theDatabase.WaitForWrites(2);
        writes.Count(x => x.ShardName == "Counters:All").ShouldBe(1);
        writes.Count(x => x.ShardName == "Others:All").ShouldBe(1);

        // A transition inside the throttle window still writes immediately
        theWriter.OnNext(transition(ShardAction.Paused, "Paused", "boom"));
        await theDatabase.WaitForWrites(3);

        // And once the interval passes, heartbeats flow again
        theTime.Advance(theWriter.HeartbeatWriteInterval + TimeSpan.FromMilliseconds(1));
        theWriter.OnNext(heartbeat());
        var all = await theDatabase.WaitForWrites(4);
        all.Count(x => x.ShardName == "Counters:All").ShouldBe(3);
    }

    [Fact]
    public async Task a_throwing_database_write_is_swallowed_and_does_not_stop_subsequent_writes()
    {
        theDatabase.FailNextWrite = true;

        theWriter.OnNext(transition(ShardAction.Started, "Running"));
        theWriter.OnNext(transition(ShardAction.Stopped, "Stopped"));

        // The first write threw, so only the second lands — and the writer kept going
        var writes = await theDatabase.WaitForWrites(1);
        writes[0].AgentStatus.ShouldBe("Stopped");
    }

    [Fact]
    public async Task carries_the_assigned_node_number_into_running_on_node()
    {
        var state = transition(ShardAction.Started, "Running");
        state.AssignedNodeNumber = 3;

        theWriter.OnNext(state);

        var writes = await theDatabase.WaitForWrites(1);
        writes[0].RunningOnNode.ShouldBe(3);
    }

    [Fact]
    public async Task does_not_clobber_an_explicit_running_on_node()
    {
        var state = transition(ShardAction.Started, "Running");
        state.AssignedNodeNumber = 3;
        state.RunningOnNode = 7;

        theWriter.OnNext(state);

        var writes = await theDatabase.WaitForWrites(1);
        writes[0].RunningOnNode.ShouldBe(7);
    }

    [Fact]
    public async Task subscription_agent_lifecycle_flows_through_a_tracker_into_the_database()
    {
        // End-to-end through the real tracker + a real SubscriptionAgent: start (Running heartbeat)
        // then stop (Stopped), proving the daemon-published states reach the store write
        var tracker = new ShardStateTracker(NullLogger.Instance);
        tracker.Subscribe(theWriter);

        var agent = new SubscriptionAgent(new ShardName("Counters"), new AsyncOptions(), theTime,
            Substitute.For<IEventLoader>(), Substitute.For<ISubscriptionExecution>(), tracker,
            Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);

        await agent.StartAsync(new SubscriptionExecutionRequest(0, ShardExecutionMode.Continuous,
            new ErrorHandlingOptions(), new NulloDaemonRuntime()));

        var writes = await theDatabase.WaitForWrites(1);
        writes[0].ShardName.ShouldBe("Counters:All");
        writes[0].AgentStatus.ShouldBe("Running");
        writes[0].LastHeartbeat.ShouldNotBeNull();

        await agent.StopAndDrainAsync(CancellationToken.None);

        writes = await theDatabase.WaitForWrites(2);
        writes[1].AgentStatus.ShouldBe("Stopped");
    }

    private class RecordingEventDatabase : IEventDatabase
    {
        private readonly List<ShardState> _writes = new();
        private readonly SemaphoreSlim _signal = new(0);

        public bool FailNextWrite { get; set; }

        public Task WriteExtendedProgressionAsync(ShardState state, CancellationToken token = default)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                _signal.Release();
                throw new InvalidOperationException("The database is grumpy");
            }

            lock (_writes)
            {
                _writes.Add(state);
            }

            _signal.Release();
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<ShardState>> WaitForWrites(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (_writes)
                {
                    if (_writes.Count >= count) return _writes.ToList();
                }

                await _signal.WaitAsync(timeout.Token);
            }
        }

        public async Task AssertNoWrites()
        {
            // Give the background block a beat to (not) do its thing
            await Task.Delay(100);
            lock (_writes)
            {
                _writes.ShouldBeEmpty();
            }
        }

        // Unused IEventDatabase surface
        public string Identifier => "recording";
        public Uri DatabaseUri => new("db://recording");
        public ShardStateTracker Tracker => null!;

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent,
            CancellationToken token) => Task.CompletedTask;

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token) => Task.CompletedTask;

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => Task.CompletedTask;

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => Task.FromResult(0L);

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => Task.FromResult<long?>(null);

        public string StorageIdentifier => "recording";

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token) => Task.FromResult(0L);

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ShardState>>([]);
    }
}
