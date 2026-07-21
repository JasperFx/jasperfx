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
}
