using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

public class ShardStateTrackerTests : IDisposable
{
    private readonly ShardStateTracker theTracker = new ShardStateTracker(new NulloLogger());

    public void Dispose()
    {
        theTracker.As<IDisposable>().Dispose();
    }

    [Fact]
    public async Task calls_back_to_observer()
    {
        var observer1 = new Observer();
        var observer2 = new Observer();
        var observer3 = new Observer();

        var state1 = new ShardState("foo", 35);
        var state2 = new ShardState("bar", 45);
        var state3 = new ShardState("baz", 55);

        theTracker.Subscribe(observer1);
        theTracker.Subscribe(observer2);
        theTracker.Subscribe(observer3);

        await theTracker.PublishAsync(state1);
        await theTracker.PublishAsync(state2);
        await theTracker.PublishAsync(state3);

        await theTracker.Complete();

        observer1.States.ShouldBe([state1, state2, state3]);
        observer2.States.ShouldBe([state1, state2, state3]);
        observer3.States.ShouldBe([state1, state2, state3]);

        theTracker.Finish();
    }

    [Fact]
    public async Task stamps_the_assigned_node_number_onto_published_states()
    {
        theTracker.AssignedNodeNumber = 4;

        var observer = new Observer();
        theTracker.Subscribe(observer);

        var state = new ShardState("Trip:All", 30) { AgentStatus = "Running" };
        await theTracker.PublishAsync(state);
        await theTracker.Complete();

        observer.States.Last().AssignedNodeNumber.ShouldBe(4);

        theTracker.Finish();
    }

    [Fact]
    public async Task does_not_clobber_a_state_that_already_carries_a_node()
    {
        theTracker.AssignedNodeNumber = 4;

        var observer = new Observer();
        theTracker.Subscribe(observer);

        var state = new ShardState("Trip:All", 30) { AgentStatus = "Running", AssignedNodeNumber = 9 };
        await theTracker.PublishAsync(state);
        await theTracker.Complete();

        observer.States.Last().AssignedNodeNumber.ShouldBe(9);

        theTracker.Finish();
    }

    [Fact]
    public async Task stamps_nothing_when_the_assigned_node_number_is_unset()
    {
        // Default AssignedNodeNumber == 0 (unset) — e.g. non-managed / single-node — leaves states alone.
        var observer = new Observer();
        theTracker.Subscribe(observer);

        var state = new ShardState("Trip:All", 30) { AgentStatus = "Running" };
        await theTracker.PublishAsync(state);
        await theTracker.Complete();

        observer.States.Last().AssignedNodeNumber.ShouldBe(0);

        theTracker.Finish();
    }

    [Fact]
    public async Task mark_skipping()
    {
        var observer1 = new Observer();
        var observer2 = new Observer();
        var observer3 = new Observer();
        
        theTracker.Subscribe(observer1);
        theTracker.Subscribe(observer2);
        theTracker.Subscribe(observer3);
        
        await theTracker.MarkSkippingAsync(1000, 1100);
        
        await theTracker.Complete();

        var state = observer1.States.Last();
        
        state.ShardName.ShouldBe("HighWaterMark");
        state.Action.ShouldBe(ShardAction.Skipped);
        state.PreviousGoodMark.ShouldBe(1000);
        state.Sequence.ShouldBe(1100);
    }

    [Fact]
    public async Task mark_skipping_populates_skipped_events_count()
    {
        var observer = new Observer();
        theTracker.Subscribe(observer);

        await theTracker.MarkSkippingAsync(100, 105);

        await theTracker.Complete();

        var state = observer.States.Last();
        state.SkippedEventsCount.ShouldBe(5L);
    }

    [Fact]
    public async Task mark_skipping_with_no_advance_leaves_skipped_events_count_null()
    {
        var observer = new Observer();
        theTracker.Subscribe(observer);

        await theTracker.MarkSkippingAsync(100, 100);

        await theTracker.Complete();

        var state = observer.States.Last();
        state.SkippedEventsCount.ShouldBeNull();
    }

    [Fact]
    public async Task mark_high_water_stamps_last_advanced_timestamp()
    {
        // jasperfx#449: the published HighWaterMark ShardState must carry "when the mark last advanced"
        // so a monitor can compute seconds-since-advance server-side instead of a reconnect-resetting heuristic.
        var observer = new Observer();
        theTracker.Subscribe(observer);

        var advancedAt = new DateTimeOffset(2026, 6, 13, 1, 2, 3, TimeSpan.Zero);
        await theTracker.MarkHighWaterAsync(1500, advancedAt);

        await theTracker.Complete();

        var state = observer.States.Last();
        state.ShardName.ShouldBe("HighWaterMark");
        state.Sequence.ShouldBe(1500);
        state.LastAdvanced.ShouldBe(advancedAt);
    }

    [Fact]
    public async Task mark_high_water_without_timestamp_leaves_last_advanced_null()
    {
        var observer = new Observer();
        theTracker.Subscribe(observer);

        await theTracker.MarkHighWaterAsync(1500);

        await theTracker.Complete();

        observer.States.Last().LastAdvanced.ShouldBeNull();
    }

    [Fact]
    public async Task mark_skipping_stamps_last_advanced_timestamp()
    {
        var observer = new Observer();
        theTracker.Subscribe(observer);

        var advancedAt = new DateTimeOffset(2026, 6, 13, 4, 5, 6, TimeSpan.Zero);
        await theTracker.MarkSkippingAsync(1000, 1100, advancedAt);

        await theTracker.Complete();

        observer.States.Last().LastAdvanced.ShouldBe(advancedAt);
    }

    [Fact]
    public void default_state_action_is_update()
    {
        new ShardState("foo", 22L)
            .Action.ShouldBe(ShardAction.Updated);
    }

    public class Observer: IObserver<ShardState>
    {
        public readonly IList<ShardState> States = new List<ShardState>();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value)
        {
            States.Add(value);
        }
    }

    [Fact]
    public void shard_state_skipped_events_count_defaults_to_null()
    {
        // CW#150 signal 3 semantics: null distinguishes "implementation hasn't
        // populated it" from "zero skips have happened" — CritterWatch renders
        // null as "n/a" not "0".
        new ShardState("foo", 100).SkippedEventsCount.ShouldBeNull();
    }

    [Fact]
    public void shard_state_skipped_events_count_round_trips()
    {
        var state = new ShardState(ShardState.HighWaterMark, 1_000_000)
        {
            SkippedEventsCount = 42L
        };

        state.SkippedEventsCount.ShouldBe(42L);
    }

    // CritterWatch#678 — a store backed by more than one database runs one daemon (and one tracker) per
    // database, and every one of them publishes the same shard names. Stamping the tracker's database onto
    // each state is what lets a consumer tell them apart.

    [Fact]
    public void shard_state_database_identifier_defaults_to_null()
    {
        new ShardState("foo", 100).DatabaseIdentifier.ShouldBeNull();
    }

    [Fact]
    public async Task publish_stamps_the_trackers_database_onto_every_state()
    {
        theTracker.DatabaseIdentifier = "tenant-a";
        var observer = new Observer();
        theTracker.Subscribe(observer);

        await theTracker.PublishAsync(new ShardState("foo", 35));
        // The high water agent's marks go through the same door.
        await theTracker.MarkHighWaterAsync(100);

        await theTracker.Complete();

        observer.States.Count.ShouldBe(2);
        observer.States.ShouldAllBe(x => x.DatabaseIdentifier == "tenant-a");
    }

    [Fact]
    public async Task publish_leaves_a_state_that_already_names_its_database_alone()
    {
        theTracker.DatabaseIdentifier = "tenant-a";
        var observer = new Observer();
        theTracker.Subscribe(observer);

        await theTracker.PublishAsync(new ShardState("foo", 35) { DatabaseIdentifier = "tenant-b" });
        await theTracker.PublishAsync(new ShardState("bar", 36));

        await theTracker.Complete();

        observer.States.Single(x => x.ShardName == "foo").DatabaseIdentifier.ShouldBe("tenant-b");
        observer.States.Single(x => x.ShardName == "bar").DatabaseIdentifier.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task publish_leaves_the_database_null_when_the_tracker_has_none()
    {
        var observer = new Observer();
        theTracker.Subscribe(observer);

        await theTracker.PublishAsync(new ShardState("foo", 35));

        await theTracker.Complete();

        observer.States.ShouldAllBe(x => x.DatabaseIdentifier == null);
    }

    // jasperfx#568 — "publish, then wait" is the natural shape for user code and test suites, but
    // publication is asynchronous: PublishAsync posts to a Block and returns, and the consumer thread
    // walks the listener list as it stood when it started. A watcher that subscribes after the state
    // has already been delivered never sees it, and the wait used to burn its full timeout (1 minute by
    // default) claiming a shard never reached a sequence it reached before the wait even started.
    //
    // Each of these publishes and then waits for the delivery to land in the tracker's snapshot BEFORE
    // waiting, which is the deterministic form of the race — the state is provably already published,
    // and nothing further will be published for that shard.

    /// <summary>
    /// Block until the tracker's own listener has recorded a state for the shard at or past the sequence,
    /// which is exactly the "state was published before the wait was set up" precondition.
    /// </summary>
    private async Task drainTo(string shardName, long sequence)
    {
        using var timeout = new CancellationTokenSource(10.Seconds());
        while (!timeout.IsCancellationRequested)
        {
            var current = theTracker.CurrentState(shardName);
            if (current != null && current.Sequence >= sequence) return;

            await Task.Delay(10.Milliseconds());
        }

        throw new TimeoutException($"The tracker never recorded {shardName} at sequence {sequence}");
    }

    [Fact]
    public async Task shard_status_watcher_completes_from_the_already_published_state()
    {
        // Straight at the watcher, since WaitForShardState's own pre-check would otherwise mask it:
        // subscribing after publication must still complete.
        await theTracker.PublishAsync(new ShardState("Trip:All", 45) { AgentStatus = "Running" });
        await drainTo("Trip:All", 45);

        var watcher = new ShardStatusWatcher(theTracker, new ShardState("Trip:All", 45), 5.Seconds());

        var state = await watcher.Task;
        state.ShardName.ShouldBe("Trip:All");
        state.Sequence.ShouldBe(45);
    }

    [Fact]
    public async Task wait_for_shard_state_does_not_hang_on_an_already_published_state()
    {
        await theTracker.PublishAsync(new ShardState("Trip:All", 45) { AgentStatus = "Running" });
        await drainTo("Trip:All", 45);

        var state = await theTracker.WaitForShardState("Trip:All", 45, 5.Seconds());

        state.Sequence.ShouldBe(45);
    }

    [Fact]
    public async Task wait_for_shard_condition_does_not_hang_on_an_already_published_state()
    {
        // WaitForShardCondition never checked the current state at all, so a condition already satisfied
        // by every known state waited for the *next* publication regardless of timing.
        await theTracker.PublishAsync(new ShardState("Trip:All", 45) { AgentStatus = "Paused" });
        await drainTo("Trip:All", 45);

        var state = await theTracker.WaitForShardCondition(
            x => x.ShardName == "Trip:All" && x.AgentStatus == "Paused",
            "Trip:All is paused", 5.Seconds());

        state.AgentStatus.ShouldBe("Paused");
    }

    [Fact]
    public async Task wait_for_high_water_mark_does_not_hang_on_an_already_published_mark()
    {
        await theTracker.MarkHighWaterAsync(1500);
        await drainTo(ShardState.HighWaterMark, 1500);

        var state = await theTracker.WaitForHighWaterMark(1200, 5.Seconds());

        state.Sequence.ShouldBeGreaterThanOrEqualTo(1200);
    }

    [Fact]
    public async Task wait_for_shard_state_still_waits_for_a_state_that_has_not_happened_yet()
    {
        // The re-check must not turn every wait into "complete against whatever is there now".
        await theTracker.PublishAsync(new ShardState("Trip:All", 10));
        await drainTo("Trip:All", 10);

        var waiter = theTracker.WaitForShardState("Trip:All", 45, 10.Seconds());
        waiter.IsCompleted.ShouldBeFalse();

        await theTracker.PublishAsync(new ShardState("Trip:All", 45));

        (await waiter).Sequence.ShouldBe(45);
    }

    [Fact]
    public async Task wait_for_shard_condition_still_waits_for_a_condition_not_yet_met()
    {
        await theTracker.PublishAsync(new ShardState("Trip:All", 10) { AgentStatus = "Running" });
        await drainTo("Trip:All", 10);

        var waiter = theTracker.WaitForShardCondition(
            x => x.ShardName == "Trip:All" && x.AgentStatus == "Paused", "Trip:All is paused", 10.Seconds());
        waiter.IsCompleted.ShouldBeFalse();

        await theTracker.PublishAsync(new ShardState("Trip:All", 11) { AgentStatus = "Paused" });

        (await waiter).AgentStatus.ShouldBe("Paused");
    }

    [Fact]
    public async Task a_condition_that_blows_up_on_an_unrelated_state_is_still_just_a_miss()
    {
        // The tracker has always swallowed and logged whatever a listener throws, so a condition that
        // is only safe for its own shard used to mean "no match yet". Re-checking the snapshot must not
        // turn that into an exception thrown straight out of WaitForShardCondition.
        await theTracker.PublishAsync(new ShardState("Other:All", 10));
        await drainTo("Other:All", 10);

        var waiter = theTracker.WaitForShardCondition(
            x => x.AgentStatus!.Length > 0 && x.ShardName == "Trip:All", "Trip:All reports a status",
            10.Seconds());
        waiter.IsCompleted.ShouldBeFalse();

        await theTracker.PublishAsync(new ShardState("Trip:All", 11) { AgentStatus = "Running" });

        (await waiter).ShardName.ShouldBe("Trip:All");
    }
}
