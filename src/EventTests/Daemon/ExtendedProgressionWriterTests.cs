using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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

    private static ShardState heartbeat(string shardName = "Counters:All", long sequence = 42)
    {
        return new ShardState(shardName, sequence)
        {
            Action = ShardAction.Updated,
            AgentStatus = "Running",
            LastHeartbeat = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public async Task writes_status_transitions_through_the_database_immediately()
    {
        theWriter.OnNext(transition(ShardAction.Started, "Running"));
        theWriter.OnNext(transition(ShardAction.Paused, "Paused", "boom"));
        theWriter.OnNext(transition(ShardAction.Stopped, "Stopped"));

        var writes = await theDatabase.WaitForWrites(3);

        writes[0].AgentStatus.ShouldBe("Running");
        writes[1].AgentStatus.ShouldBe("Paused");
        writes[1].PauseReason.ShouldBe("boom");
        writes[2].AgentStatus.ShouldBe("Stopped");

        // A transition never waits on the flush interval: three transitions, three batches
        (await theDatabase.Batches()).Count.ShouldBe(3);
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
    public async Task coalesces_heartbeats_into_one_batched_write_per_flush_interval()
    {
        // The very first heartbeat flushes immediately (nothing to wait for)
        theWriter.OnNext(heartbeat(sequence: 1));
        var writes = await theDatabase.WaitForWrites(1);
        writes[0].Sequence.ShouldBe(1);

        // Everything landing inside the flush interval is coalesced, latest state per shard wins
        theWriter.OnNext(heartbeat(sequence: 2));
        theWriter.OnNext(heartbeat(sequence: 3));
        theWriter.OnNext(heartbeat("Others:All", sequence: 7));
        await theDatabase.AssertWriteCountStaysAt(1);

        // Once the interval passes, the next publication flushes the whole pending set as ONE batch
        theTime.Advance(theWriter.HeartbeatWriteInterval + TimeSpan.FromMilliseconds(1));
        theWriter.OnNext(heartbeat("Others:All", sequence: 8));

        writes = await theDatabase.WaitForWrites(3);
        var batches = await theDatabase.Batches();
        batches.Count.ShouldBe(2);
        batches[1].Length.ShouldBe(2);
        batches[1].Single(x => x.ShardName == "Counters:All").Sequence.ShouldBe(3);
        batches[1].Single(x => x.ShardName == "Others:All").Sequence.ShouldBe(8);
        writes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task a_transition_flushes_immediately_and_carries_the_pending_heartbeats_along()
    {
        // Seed a flush so the interval throttle is active
        theWriter.OnNext(heartbeat(sequence: 1));
        await theDatabase.WaitForWrites(1);

        // These queue up inside the interval
        theWriter.OnNext(heartbeat("Others:All", sequence: 7));

        // A transition inside the interval must not wait -- and takes the pending batch with it
        theWriter.OnNext(transition(ShardAction.Paused, "Paused", "boom"));

        var writes = await theDatabase.WaitForWrites(3);
        var batches = await theDatabase.Batches();
        batches.Count.ShouldBe(2);
        batches[1].Single(x => x.ShardName == "Others:All").Sequence.ShouldBe(7);
        batches[1].Single(x => x.ShardName == "Counters:All").AgentStatus.ShouldBe("Paused");
        writes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task a_transition_replaces_a_pending_heartbeat_for_the_same_shard()
    {
        theWriter.OnNext(heartbeat(sequence: 1));
        await theDatabase.WaitForWrites(1);

        // Heartbeat queued, then a transition for the SAME shard lands: latest state wins,
        // the stale heartbeat must not overwrite the Paused status afterwards
        theWriter.OnNext(heartbeat(sequence: 2));
        theWriter.OnNext(transition(ShardAction.Paused, "Paused", "boom"));

        var writes = await theDatabase.WaitForWrites(2);
        var batches = await theDatabase.Batches();
        batches.Count.ShouldBe(2);
        batches[1].Length.ShouldBe(1);
        batches[1][0].AgentStatus.ShouldBe("Paused");
        writes.Count.ShouldBe(2);
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
    public async Task disposing_flushes_the_pending_batch()
    {
        theWriter.OnNext(heartbeat(sequence: 1));
        await theDatabase.WaitForWrites(1);

        // Queued inside the interval, would normally wait for the next flush
        theWriter.OnNext(heartbeat(sequence: 2));

        await theWriter.DisposeAsync();

        var writes = await theDatabase.WaitForWrites(2);
        writes[1].Sequence.ShouldBe(2);
    }

    [Fact]
    public async Task dispose_awaits_the_in_flight_write_rather_than_draining_in_the_background()
    {
        // jasperfx#557: DisposeAsync must not return until the queued Stopped write has actually been
        // persisted. If it drains in the background, that late Stopped write can overtake a later
        // deliberate write to the same progression row and silently roll the status back to Stopped.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theDatabase.GateWrites = gate;

        theWriter.OnNext(transition(ShardAction.Stopped, "Stopped"));

        var dispose = theWriter.DisposeAsync().AsTask();

        // The write is parked on the gate, so DisposeAsync cannot have completed yet
        await Task.Delay(100);
        dispose.IsCompleted.ShouldBeFalse();

        // Let the write through; only now may DisposeAsync complete
        gate.SetResult();
        await dispose;

        var writes = await theDatabase.WaitForWrites(1);
        writes[0].AgentStatus.ShouldBe("Stopped");
    }

    [Fact]
    public async Task the_default_batch_implementation_degrades_to_single_state_writes()
    {
        // A store that has not implemented the batched overload keeps working through the
        // default interface member, one single-state write per shard
        var singlesOnly = new SingleWriteOnlyDatabase();
        IEventDatabase database = singlesOnly;

        await database.WriteExtendedProgressionAsync([heartbeat(sequence: 1), heartbeat("Others:All", sequence: 2)],
            CancellationToken.None);

        singlesOnly.Writes.Count.ShouldBe(2);
        singlesOnly.Writes[0].ShardName.ShouldBe("Counters:All");
        singlesOnly.Writes[1].ShardName.ShouldBe("Others:All");
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

    private class SingleWriteOnlyDatabase : IEventDatabase
    {
        public List<ShardState> Writes { get; } = new();

        public Task WriteExtendedProgressionAsync(ShardState state, CancellationToken token = default)
        {
            Writes.Add(state);
            return Task.CompletedTask;
        }

        public string Identifier => "singles";
        public Uri DatabaseUri => new("db://singles");
        public ShardStateTracker Tracker => null!;

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent,
            CancellationToken token) => Task.CompletedTask;

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token) => Task.CompletedTask;

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => Task.CompletedTask;

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => Task.FromResult(0L);

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => Task.FromResult<long?>(null);

        public string StorageIdentifier => "singles";

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token) => Task.FromResult(0L);

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ShardState>>([]);
    }

    private class RecordingEventDatabase : IEventDatabase
    {
        private readonly List<ShardState[]> _batches = new();
        private readonly SemaphoreSlim _signal = new(0);

        public bool FailNextWrite { get; set; }

        // When set, a write parks on this gate before it records anything, so a test can observe whether
        // DisposeAsync waits for the in-flight write to finish (jasperfx#557).
        public TaskCompletionSource? GateWrites { get; set; }

        public async Task WriteExtendedProgressionAsync(IReadOnlyList<ShardState> states,
            CancellationToken token = default)
        {
            if (GateWrites != null)
            {
                await GateWrites.Task;
            }

            if (FailNextWrite)
            {
                FailNextWrite = false;
                _signal.Release();
                throw new InvalidOperationException("The database is grumpy");
            }

            lock (_batches)
            {
                _batches.Add(states.ToArray());
            }

            _signal.Release();
        }

        public async Task<IReadOnlyList<ShardState>> WaitForWrites(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (_batches)
                {
                    var flattened = _batches.SelectMany(x => x).ToList();
                    if (flattened.Count >= count) return flattened;
                }

                await _signal.WaitAsync(timeout.Token);
            }
        }

        public async Task<IReadOnlyList<ShardState[]>> Batches()
        {
            // Give the background block a beat to finish posting
            await Task.Delay(50);
            lock (_batches)
            {
                return _batches.ToList();
            }
        }

        public async Task AssertNoWrites()
        {
            // Give the background block a beat to (not) do its thing
            await Task.Delay(100);
            lock (_batches)
            {
                _batches.ShouldBeEmpty();
            }
        }

        public async Task AssertWriteCountStaysAt(int count)
        {
            await Task.Delay(100);
            lock (_batches)
            {
                _batches.SelectMany(x => x).Count().ShouldBe(count);
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
