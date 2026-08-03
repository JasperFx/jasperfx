using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#598/#610 item 2: a warm-up concurrency control independent of agent-start batching.
//
// Before #598 the number of shards simultaneously running the blue/green side-effect gate's warm-up was
// emergent rather than chosen — it was however many gate-needing shards happened to land in whatever
// chunk the distribution layer was starting, which made it a function of the host's agent-start batch
// size and of what fraction of the fleet belonged to the bumped projection. A field deployment measured
// 65 concurrent warm-ups that way, with no setting an operator could turn.
//
// DaemonSettings.MaxConcurrentSideEffectGateWarmupsPerDatabase is that setting. Crucially, waiting for a
// slot must NOT block the agent: it is started, registered and observable throughout — it simply is not
// one of the N shards replaying right now. These tests drive real SubscriptionAgents over a shared
// throttle to prove both halves.
public class SideEffectGateWarmupThrottleTests
{
    private const long GateMark = 1000;
    private const long HighWater = 1500;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task a_bounded_throttle_admits_one_shard_at_a_time_and_hands_the_slot_on_when_a_gate_lifts()
    {
        using var throttle = new SemaphoreSlim(1);

        await using var first = new GatedAgent(throttle);
        await using var second = new GatedAgent(throttle);

        first.HoldPages();
        second.HoldPages();

        await first.StartGatedAsync();
        await waitFor(() => first.LoadAttempts > 0, "the first shard to take the only warm-up slot");

        // The second shard starts perfectly normally — it is Running and would answer a supervisor — but
        // it is not one of the shards warming up, so it must not be pulling pages.
        await second.StartGatedAsync();
        second.Agent.Status.ShouldBe(AgentStatus.Running);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        second.LoadAttempts.ShouldBe(0);

        // Let the first shard run its suppressed catch-up to the prior version's mark. Crossing it is what
        // releases the slot, so the second shard picks it up without any operator action.
        first.ReleasePages();
        await waitFor(() => !first.Agent.SideEffectsSuppressed, "the first shard's gate to lift");
        await waitFor(() => second.LoadAttempts > 0, "the second shard to inherit the warm-up slot");
    }

    [Fact]
    public async Task an_unbounded_throttle_is_the_default_and_lets_every_gated_shard_warm_up_at_once()
    {
        await using var first = new GatedAgent(null);
        await using var second = new GatedAgent(null);

        first.HoldPages();
        second.HoldPages();

        await first.StartGatedAsync();
        await second.StartGatedAsync();

        await waitFor(() => first.LoadAttempts > 0 && second.LoadAttempts > 0,
            "both shards to warm up concurrently");
    }

    [Fact]
    public async Task a_shard_torn_down_mid_warm_up_hands_its_slot_back()
    {
        // Otherwise one stopped shard would starve the throttle for the daemon's whole lifetime — the
        // failure mode a bound has to be free of before it is safe to recommend one.
        using var throttle = new SemaphoreSlim(1);

        await using var second = new GatedAgent(throttle);

        var first = new GatedAgent(throttle);
        first.HoldPages();
        await first.StartGatedAsync();
        await waitFor(() => first.LoadAttempts > 0, "the first shard to take the only warm-up slot");

        await first.DisposeAsync();

        second.HoldPages();
        await second.StartGatedAsync();
        await waitFor(() => second.LoadAttempts > 0, "the released slot to reach the second shard");
    }

    [Fact]
    public async Task a_shard_stopped_and_drained_mid_warm_up_hands_its_slot_back()
    {
        // A graceful stop does not dispose the agent — the daemon simply drops it from its registry — so
        // StopAndDrainAsync has to release the slot on its own or a routine shard stop leaks one.
        using var throttle = new SemaphoreSlim(1);

        await using var first = new GatedAgent(throttle);
        await using var second = new GatedAgent(throttle);

        first.HoldPages();
        await first.StartGatedAsync();
        await waitFor(() => first.LoadAttempts > 0, "the first shard to take the only warm-up slot");

        await first.Agent.StopAndDrainAsync(TestContext.Current.CancellationToken);

        second.HoldPages();
        await second.StartGatedAsync();
        await waitFor(() => second.LoadAttempts > 0, "the released slot to reach the second shard");
    }

    [Fact]
    public async Task a_shard_still_queued_for_a_slot_tears_down_cleanly()
    {
        // The waiter is parked on the semaphore off the command loop; disposing must cancel it rather
        // than leave it to be granted a slot no one will ever hand back.
        using var throttle = new SemaphoreSlim(1);

        await using var holder = new GatedAgent(throttle);
        holder.HoldPages();
        await holder.StartGatedAsync();
        await waitFor(() => holder.LoadAttempts > 0, "the holder to take the only warm-up slot");

        var queued = new GatedAgent(throttle);
        queued.HoldPages();
        await queued.StartGatedAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(async () => await queued.DisposeAsync());

        // The holder still owns the slot, so nothing was double-released.
        throttle.CurrentCount.ShouldBe(0);
    }

    [Fact]
    public async Task the_gate_flip_is_driven_by_committed_progression_not_by_what_was_loaded()
    {
        // Committed is what a restart re-reads, so it is the only mark that makes an interrupted warm-up
        // safe to resume. Held pages mean plenty has been ENQUEUED past nothing and nothing committed —
        // the agent must still be suppressing.
        await using var agent = new GatedAgent(null);
        agent.HoldPages();
        await agent.StartGatedAsync();
        await waitFor(() => agent.LoadAttempts > 0, "the shard to start its warm-up");

        agent.Agent.SideEffectsSuppressed.ShouldBeTrue();

        agent.ReleasePages();
        await waitFor(() => agent.Agent.LastCommitted >= GateMark, "the warm-up to reach the prior mark");
        agent.Agent.SideEffectsSuppressed.ShouldBeFalse();

        // Everything up to the mark ran suppressed, everything past it ran live.
        agent.Pages.Where(x => x.Ceiling <= GateMark).ShouldAllBe(x => x.Suppressed);
        agent.Pages.Where(x => x.Floor >= GateMark).ShouldAllBe(x => !x.Suppressed);
    }

    private static async Task waitFor(Func<bool> condition, string description)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            if (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {description}");
            }

            await Task.Delay(20, CancellationToken.None);
        }
    }

    // A real SubscriptionAgent armed with the side-effect gate, over a loader whose pages can be held and
    // an execution that acknowledges everything it is handed.
    private sealed class GatedAgent : IAsyncDisposable
    {
        private readonly HoldableLoader _loader = new();
        private readonly IDaemonRuntime _runtime = Substitute.For<IDaemonRuntime>();

        public GatedAgent(SemaphoreSlim? throttle)
        {
            _runtime.SideEffectGateWarmupThrottle.Returns(throttle);

            var execution = new AcknowledgingExecution(this);

            Agent = new SubscriptionAgent(new ShardName("Trips", ShardName.All, 3, null),
                new AsyncOptions { BatchSize = 200 }, TimeProvider.System, _loader, execution,
                new ShardStateTracker(NullLogger.Instance), Substitute.For<ISubscriptionMetrics>(),
                NullLogger.Instance);
        }

        public SubscriptionAgent Agent { get; }

        public ConcurrentQueue<(bool Suppressed, long Floor, long Ceiling)> Pages { get; } = new();

        public int LoadAttempts => _loader.Attempts;

        public void HoldPages() => _loader.Hold();

        public void ReleasePages() => _loader.Release();

        public Task StartGatedAsync()
        {
            var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Continuous,
                new ErrorHandlingOptions(), _runtime)
            {
                StartingHighWater = HighWater,
                SideEffectGateMark = GateMark
            };

            return Agent.StartAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _loader.Release();
            await Agent.DisposeAsync();
        }

        private sealed class AcknowledgingExecution : ISubscriptionExecution
        {
            private readonly GatedAgent _parent;

            public AcknowledgingExecution(GatedAgent parent) => _parent = parent;

            public ShardName ShardName => _parent.Agent.Name;

            public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

            public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
            {
                _parent.Pages.Enqueue((subscriptionAgent.SideEffectsSuppressed, page.Floor, page.Ceiling));
                return subscriptionAgent.MarkSuccessAsync(page.Ceiling);
            }

            public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;

            public Task HardStopAsync() => Task.CompletedTask;

            public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
            {
                executor = null;
                return false;
            }

            public Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
                CancellationToken cancellation) => Task.CompletedTask;

            public Task ProcessRangeAsync(EventRange range) => Task.CompletedTask;

            public bool TryGetAggregateCache<TId, TDoc>(
                [NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
            {
                caching = null;
                return false;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class HoldableLoader : IEventLoader
    {
        private volatile TaskCompletionSource? _hold;
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _hold?.TrySetResult();

        public async Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref _attempts);

            var hold = _hold;
            if (hold != null)
            {
                await hold.Task.WaitAsync(token);
            }

            var page = new EventPage(request.Floor);
            var target = Math.Min(request.Floor + request.BatchSize, request.HighWater);
            for (var sequence = request.Floor + 1; sequence <= target; sequence++)
            {
                page.Add(new Event<AEvent>(new AEvent()) { Sequence = sequence });
            }

            page.CalculateCeiling(request.BatchSize, request.HighWater);
            return page;
        }
    }
}
