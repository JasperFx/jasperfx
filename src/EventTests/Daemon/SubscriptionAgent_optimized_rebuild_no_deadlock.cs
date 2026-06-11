using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// Regression for marten#4721: the continuous-startup "optimized rebuild" for a composite
// projection used to run INLINE inside the agent's single-consumer command-loop handler
// (Apply for CommandType.Start). The replay posts one RangeCompleted command per page back
// into the same bounded (capacity 10_000) command channel whose only reader is that very
// handler — so once the channel filled, the post blocked, the reader was blocked awaiting it,
// and the shard silently wedged at a batch boundary (idle: no query, no lock, no exception).
//
// The fix runs the rebuild on a task SEPARATE from the command-loop consumer, so the consumer
// is free to drain those RangeCompleted posts. This test drives the agent through its real
// command block (via StartAsync) with a replay executor that emits MORE pages than the channel
// capacity; under the old inline code it deadlocks (LastCommitted never advances past 0 and the
// test times out), under the fix it drains and catches up.
public class SubscriptionAgent_optimized_rebuild_no_deadlock
{
    // Bigger than the Block<Command> bounded-channel capacity (10_000) so an inline replay would
    // overflow the channel and self-deadlock.
    private const int PageCount = 12_000;

    [Fact]
    public async Task optimized_rebuild_runs_off_the_command_loop_and_does_not_deadlock()
    {
        var execution = Substitute.For<ISubscriptionExecution>();
        var replay = new FakeReplayExecutor(PageCount);

        execution.TryBuildReplayExecutor(out Arg.Any<IReplayExecutor?>())
            .Returns(call =>
            {
                call[0] = replay;
                return true;
            });

        var agent = new SubscriptionAgent(new ShardName("Composite1"), new AsyncOptions(), TimeProvider.System,
            Substitute.For<IEventLoader>(), execution, new ShardStateTracker(NullLogger.Instance),
            Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);

        // Floor 0 + a non-zero high water => the Start handler triggers the optimized rebuild.
        var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Continuous, new ErrorHandlingOptions(),
            new NulloDaemonRuntime()) { StartingHighWater = PageCount };

        await agent.StartAsync(request);

        // Wait for the off-consumer rebuild to drain every page through the command channel. Under
        // the pre-fix inline implementation this never advances past 0 and the wait times out.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (agent.LastCommitted < PageCount && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        agent.LastCommitted.ShouldBe(PageCount);
        replay.Completed.ShouldBeTrue();

        await agent.DisposeAsync();
    }

    private sealed class FakeReplayExecutor : IReplayExecutor
    {
        private readonly int _pages;

        public FakeReplayExecutor(int pages)
        {
            _pages = pages;
        }

        public bool Completed { get; private set; }

        public async Task StartAsync(SubscriptionExecutionRequest request, ISubscriptionController controller,
            CancellationToken cancellation)
        {
            // Mimic CompositeReplayExecutor: one MarkSuccessAsync (== a RangeCompleted post back into
            // the agent's command block) per page, all the way up to the ceiling.
            for (var seq = 1; seq <= _pages; seq++)
            {
                await controller.MarkSuccessAsync(seq).ConfigureAwait(false);
            }

            Completed = true;
        }
    }
}
