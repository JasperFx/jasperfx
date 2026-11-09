using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace StressTests;

// Moved from EventTests' ShardStateTrackerTests.
public class ShardStateTrackerStressTests
{
    [Fact]
    public async Task publish_then_wait_from_another_thread_never_loses_the_wakeup()
    {
        // The shape Marten's HighWaterAgentTests hit: a publication in flight on one thread while the wait
        // is being set up on another, and nothing further is ever published for that shard. Repeat it
        // enough times to land inside the old window.
        for (var i = 0; i < 200; i++)
        {
            using var tracker = new ShardStateTracker(NullLogger.Instance);
            var ready = new ManualResetEventSlim(false);

            var publisher = Task.Run(async () =>
            {
                ready.Wait();
                await tracker.PublishAsync(new ShardState("Trip:All", 45) { AgentStatus = "Running" });
            }, TestContext.Current.CancellationToken);

            ready.Set();

            var state = await tracker.WaitForShardState("Trip:All", 45, 10.Seconds());
            state.Sequence.ShouldBe(45);

            await publisher;
        }
    }

    [Fact]
    public async Task concurrent_subscribers_are_never_silently_dropped()
    {
        // Subscribe was a read-modify-write on a plain field, so two threads adding themselves at once
        // could leave one of them off the list entirely — a permanently lost wakeup, not a delayed one.
        using var tracker = new ShardStateTracker(NullLogger.Instance);
        var observers = Enumerable.Range(0, 100).Select(_ => new Observer()).ToArray();

        Parallel.ForEach(observers, observer => tracker.Subscribe(observer));

        await tracker.PublishAsync(new ShardState("Trip:All", 45));
        await tracker.Complete();

        observers.ShouldAllBe(x => x.States.Count == 1);
    }

    private class Observer : IObserver<ShardState>
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
