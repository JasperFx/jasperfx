using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#681: ShardMode.rebuilding had no writer anywhere in the tree, so every ShardState a
// SubscriptionAgent published claimed `continuous` -- including the ones published during a replay.
// A subscriber watching ShardStateTracker therefore could not tell a projection catching up under a
// rebuild from one running normally, which is the only distinction the enum exists to draw.
//
// The agent already knew; it just knew on a different enum (ShardExecutionMode) that was never
// copied onto what it published. So these assert propagation, not new state -- and they assert it on
// what comes out of the tracker rather than on the agent's own property, because the property was
// always right and the published state was always wrong.
public class ShardStateModeTests
{
    private const long HighWater = 1000;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly RecordingObserver theObserver = new();
    private readonly ShardStateTracker theTracker;

    public ShardStateModeTests()
    {
        theTracker = new ShardStateTracker(NullLogger.Instance);
        theTracker.Subscribe(theObserver);
    }

    private SubscriptionAgent agentFor()
        => new(new ShardName("Projection1"), new AsyncOptions(), TimeProvider.System,
            new ExhaustingEventLoader(), Substitute.For<ISubscriptionExecution>(),
            theTracker, Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);

    private static SubscriptionExecutionRequest requestFor(ShardExecutionMode mode)
        => new(0, mode, new ErrorHandlingOptions(), new NulloDaemonRuntime());

    [Fact]
    public async Task a_continuous_start_publishes_continuous()
    {
        await using var agent = agentFor();

        await agent.StartAsync(requestFor(ShardExecutionMode.Continuous));

        (await theObserver.WaitForAsync(ShardAction.Started)).Mode.ShouldBe(ShardMode.continuous);
    }

    /// <remarks>
    /// The distinction the issue is actually about. A shard behind the high water mark under normal
    /// operation is still <c>continuous</c> — the number an operator is watching is lag. Only a
    /// rebuild makes it catch-up progress instead, so only a rebuild changes the mode.
    /// </remarks>
    [Fact]
    public async Task a_catch_up_start_publishes_continuous_rather_than_rebuilding()
    {
        await using var agent = agentFor();

        await agent.StartAsync(requestFor(ShardExecutionMode.CatchUp));

        (await theObserver.WaitForAsync(ShardAction.Started)).Mode.ShouldBe(ShardMode.continuous);
    }

    /// <remarks>
    /// THE regression. Before this, the state a replay published went out with the default
    /// <c>continuous</c>, so nothing downstream could see that a rebuild was underway.
    /// </remarks>
    [Fact]
    public async Task a_replay_publishes_rebuilding()
    {
        await using var agent = agentFor();

        var replay = agent.ReplayAsync(requestFor(ShardExecutionMode.Rebuild), HighWater, TestTimeout);

        (await theObserver.WaitForAsync(ShardAction.Started)).Mode.ShouldBe(ShardMode.rebuilding);

        await finishRebuildAsync(agent, replay);
    }

    // Drive the rebuild to its ceiling so ReplayAsync returns normally. Letting it time out or fault
    // instead would leave the assertions racing the unwind, which resets Mode on its way out.
    private static async Task finishRebuildAsync(SubscriptionAgent agent, Task replay)
    {
        await agent.MarkSuccessAsync(HighWater);
        await replay.WaitAsync(TestTimeout);
    }

    /// <remarks>
    /// Every publish in the agent runs through one stamping helper, so per-batch progress carries the
    /// mode too. This is the one that matters most in practice: it is the state a progress display
    /// updates on, and the one a consumer would be keying "rebuilding" off.
    /// </remarks>
    [Fact]
    public async Task per_batch_progress_during_a_rebuild_publishes_rebuilding()
    {
        await using var agent = agentFor();

        var replay = agent.ReplayAsync(requestFor(ShardExecutionMode.Rebuild), HighWater, TestTimeout);
        await theObserver.WaitForAsync(ShardAction.Started);

        await agent.MarkSuccessAsync(250);

        (await theObserver.WaitForAsync(ShardAction.Updated, 250)).Mode.ShouldBe(ShardMode.rebuilding);

        await finishRebuildAsync(agent, replay);
    }

    [Fact]
    public async Task per_batch_progress_outside_a_rebuild_publishes_continuous()
    {
        await using var agent = agentFor();
        await agent.StartAsync(requestFor(ShardExecutionMode.Continuous));

        await agent.MarkSuccessAsync(250);

        (await theObserver.WaitForAsync(ShardAction.Updated, 250)).Mode.ShouldBe(ShardMode.continuous);
    }

    /// <remarks>
    /// The half that is easy to leave out, and worse than the original bug if you do. The teardown a
    /// finished replay runs publishes a Stopped state; if that still said <c>rebuilding</c>, a
    /// consumer tracking the last state it saw would latch on "rebuilding" permanently — a projection
    /// that never stops rebuilding, from a rebuild that ended cleanly.
    /// </remarks>
    [Fact]
    public async Task a_finished_replay_stops_reporting_rebuilding()
    {
        await using var agent = agentFor();

        var replay = agent.ReplayAsync(requestFor(ShardExecutionMode.Rebuild), HighWater, TestTimeout);
        await theObserver.WaitForAsync(ShardAction.Started);
        await finishRebuildAsync(agent, replay);

        agent.Mode.ShouldBe(ShardExecutionMode.Continuous);

        // And a state published from here on says so, rather than inheriting the finished rebuild.
        await agent.MarkSuccessAsync(900);
        (await theObserver.WaitForAsync(ShardAction.Updated, 900)).Mode.ShouldBe(ShardMode.continuous);
    }

    /// <remarks>
    /// A failure is exactly when an operator reads the state, and a rebuild that failed is a
    /// materially different thing to be told about than a running projection that failed.
    /// </remarks>
    [Fact]
    public async Task a_failure_during_a_rebuild_publishes_rebuilding()
    {
        await using var agent = agentFor();

        var replay = agent.ReplayAsync(requestFor(ShardExecutionMode.Rebuild), HighWater, TestTimeout);
        await theObserver.WaitForAsync(ShardAction.Started);

        await agent.ReportCriticalFailureAsync(new DivideByZeroException("boom"));

        (await theObserver.WaitForAsync(ShardAction.Paused)).Mode.ShouldBe(ShardMode.rebuilding);

        // The failure faults the rebuild, which is ReplayAsync's contract (see ReplayFaultPropagationTests).
        (await Should.ThrowAsync<DivideByZeroException>(replay.WaitAsync(TestTimeout))).Message.ShouldBe("boom");
    }

    // Returns one empty page that exhausts the whole range, so the agent stops loading and sits in
    // Rebuild mode until the test drives it. A substituted IEventLoader returns a null page instead,
    // which faults the agent and unwinds the replay mid-assertion.
    private class ExhaustingEventLoader: IEventLoader
    {
        public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            var page = new EventPage(request.Floor);
            page.CalculateCeiling(request.BatchSize, request.HighWater);
            return Task.FromResult(page);
        }
    }

    private class RecordingObserver: IObserver<ShardState>
    {
        private readonly ConcurrentQueue<ShardState> _states = new();

        public void OnNext(ShardState value) => _states.Enqueue(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        /// <summary>
        /// Wait for a matching publication rather than reading the queue as it stands.
        /// <see cref="ShardStateTracker.PublishAsync" /> hands the state to observers off the calling
        /// path, so an assertion that reads immediately after the awaited publish is racing the walk —
        /// and loses often enough to be useless as a regression test.
        /// </summary>
        public async Task<ShardState> WaitForAsync(ShardAction action, long? sequence = null)
        {
            bool matches(ShardState x) => x.Action == action && (sequence == null || x.Sequence == sequence);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!_states.Any(matches))
            {
                if (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"No published state with Action {action}{(sequence == null ? "" : $" at sequence {sequence}")}. Saw: {string.Join(", ", _states.Select(x => $"{x.Action}@{x.Sequence}"))}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            return _states.Last(matches);
        }
    }
}
