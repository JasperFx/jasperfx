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
}
