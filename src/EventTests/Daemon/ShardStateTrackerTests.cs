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
}
