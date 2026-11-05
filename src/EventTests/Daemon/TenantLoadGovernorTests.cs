using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// DaemonSettings.TenantLoadBudget: per-tenant agents reserve a page of a store-wide event budget before they load,
// wait in line when it is tight, and are let back in one page at a time, in turn.
public class TenantLoadGovernorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void reservations_go_straight_through_while_there_is_room()
    {
        var governor = new TenantLoadGovernor(300);

        governor.TryReserve("a", 100, () => throw new InvalidOperationException()).ShouldBeTrue();
        governor.TryReserve("b", 100, () => throw new InvalidOperationException()).ShouldBeTrue();
        governor.TryReserve("c", 100, () => throw new InvalidOperationException()).ShouldBeTrue();

        governor.InFlight.ShouldBe(300);
        governor.Waiting.ShouldBe(0);
    }

    [Fact]
    public async Task waiters_are_let_in_first_come_first_served_as_room_is_given_back()
    {
        var governor = new TenantLoadGovernor(200);
        var granted = new ConcurrentQueue<string>();

        governor.TryReserve("a", 200, () => { }).ShouldBeTrue();

        governor.TryReserve("b", 100, () => granted.Enqueue("b")).ShouldBeFalse();
        governor.TryReserve("c", 100, () => granted.Enqueue("c")).ShouldBeFalse();
        governor.TryReserve("d", 100, () => granted.Enqueue("d")).ShouldBeFalse();

        // Already in line: not queued twice
        governor.TryReserve("b", 100, () => granted.Enqueue("b again")).ShouldBeFalse();
        governor.Waiting.ShouldBe(3);

        // The callbacks of one release run in parallel, so only the set they come from is ordered
        governor.Release(200);
        await waitFor(() => granted.Count == 2);
        granted.ShouldBe(["b", "c"], ignoreOrder: true);
        governor.InFlight.ShouldBe(200);

        governor.Release(100);
        await waitFor(() => granted.Count == 3);
        granted.Last().ShouldBe("d");
        governor.Waiting.ShouldBe(0);
    }

    [Fact]
    public async Task a_cheaper_waiter_never_passes_the_head_of_the_line()
    {
        var governor = new TenantLoadGovernor(200);
        var granted = new ConcurrentQueue<string>();

        governor.TryReserve("a", 150, () => { }).ShouldBeTrue();
        governor.TryReserve("big", 200, () => granted.Enqueue("big")).ShouldBeFalse();
        governor.TryReserve("small", 10, () => granted.Enqueue("small")).ShouldBeFalse();

        // 50 would fit the small one, but the big one is first
        await Task.Delay(50);
        granted.ShouldBeEmpty();

        governor.Release(150);
        await waitFor(() => granted.Count == 1);
        granted.ShouldBe(["big"]);
    }

    [Fact]
    public void a_reservation_is_always_granted_when_nothing_is_in_flight()
    {
        // A budget smaller than a page slows the store down to one page at a time instead of stopping it
        var governor = new TenantLoadGovernor(10);

        governor.TryReserve("a", 100, () => { }).ShouldBeTrue();
        governor.TryReserve("b", 100, () => { }).ShouldBeFalse();
    }

    [Fact]
    public async Task under_memory_pressure_nothing_new_is_handed_out_until_in_flight_events_drain()
    {
        var pressure = true;
        var governor = new TenantLoadGovernor(10_000, 0.9, () => pressure ? 0.95 : 0.5);
        var granted = new ConcurrentQueue<string>();

        governor.TryReserve("a", 100, () => { }).ShouldBeTrue();
        governor.TryReserve("b", 100, () => granted.Enqueue("b")).ShouldBeFalse();

        governor.Release(100);
        await waitFor(() => granted.Count == 1);

        // One at a time while the pressure lasts
        governor.TryReserve("c", 100, () => granted.Enqueue("c")).ShouldBeFalse();

        pressure = false;
        governor.Release(100);
        await waitFor(() => granted.Count == 2);
        governor.TryReserve("d", 100, () => { }).ShouldBeTrue();
    }

    [Fact]
    public async Task withdrawing_from_the_line_lets_whoever_is_behind_in()
    {
        var governor = new TenantLoadGovernor(100);
        var granted = new ConcurrentQueue<string>();

        governor.TryReserve("a", 50, () => { }).ShouldBeTrue();
        governor.TryReserve("big", 100, () => granted.Enqueue("big")).ShouldBeFalse();
        governor.TryReserve("small", 50, () => granted.Enqueue("small")).ShouldBeFalse();

        governor.Withdraw("big").ShouldBeTrue();
        await waitFor(() => granted.Count == 1);
        granted.ShouldBe(["small"]);

        governor.Withdraw("big").ShouldBeFalse();
    }

    [Fact]
    public async Task an_async_reservation_cancelled_while_waiting_holds_nothing()
    {
        var governor = new TenantLoadGovernor(100);
        governor.TryReserve("a", 100, () => { }).ShouldBeTrue();

        using var cancellation = new CancellationTokenSource();
        var waiting = governor.ReserveAsync(100, cancellation.Token);
        await waitFor(() => governor.Waiting == 1);

        await cancellation.CancelAsync();
        (await waiting.WaitAsync(Timeout)).ShouldBeFalse();

        governor.Waiting.ShouldBe(0);
        governor.InFlight.ShouldBe(100);

        governor.Release(100);
        (await governor.ReserveAsync(100, CancellationToken.None).WaitAsync(Timeout)).ShouldBeTrue();
    }

    [Fact]
    public async Task tenant_agents_hold_no_more_than_the_budget_and_wait_running_for_their_turn()
    {
        var governor = new TenantLoadGovernor(200);
        var agents = Enumerable.Range(0, 4).Select(i => new TenantAgent(governor, $"t{i}")).ToArray();

        foreach (var agent in agents) agent.HoldCommits();
        foreach (var agent in agents) await agent.StartAsync();

        // Two pages of 100 fill the budget; everyone else waits without loading
        await waitFor(() => agents.Sum(x => x.PagesLoaded) == 2 && governor.Waiting >= 2);
        await Task.Delay(200);

        agents.Sum(x => x.PagesLoaded).ShouldBe(2);
        governor.InFlight.ShouldBe(200);
        agents.ShouldAllBe(x => x.Agent.Status == AgentStatus.Running);

        foreach (var agent in agents) agent.CommitEverything();
        await waitFor(() => agents.All(x => x.Agent.LastCommitted == TenantAgent.HighWater));

        governor.InFlight.ShouldBe(0);

        foreach (var agent in agents) await agent.DisposeAsync();
    }

    [Fact]
    public async Task tenants_take_turns_so_none_falls_behind()
    {
        var governor = new TenantLoadGovernor(100);
        var order = new ConcurrentQueue<string>();
        var agents = Enumerable.Range(0, 4).Select(i => new TenantAgent(governor, $"t{i}", order)).ToArray();

        foreach (var agent in agents) agent.HoldCommits();
        foreach (var agent in agents) await agent.StartAsync();
        await waitFor(() => governor.Waiting == 3);

        foreach (var agent in agents) agent.CommitEverything();
        await waitFor(() => agents.All(x => x.Agent.LastCommitted == TenantAgent.HighWater));

        // One page in flight at a time, handed round in turn: nobody gets a third page before everyone has a first
        var loads = order.ToArray();
        var firstOfEach = agents.Max(a => Array.IndexOf(loads, a.TenantId));
        foreach (var agent in agents)
        {
            loads.Take(firstOfEach + 1).Count(x => x == agent.TenantId).ShouldBeLessThanOrEqualTo(2);
        }

        foreach (var agent in agents) await agent.DisposeAsync();
    }

    [Fact]
    public async Task a_stopped_agent_gives_back_what_it_held_and_leaves_the_line()
    {
        var governor = new TenantLoadGovernor(100);
        var holder = new TenantAgent(governor, "holder");
        var queued = new TenantAgent(governor, "queued");
        var next = new TenantAgent(governor, "next");

        holder.HoldCommits();
        queued.HoldCommits();
        next.HoldCommits();

        await holder.StartAsync();
        await waitFor(() => holder.PagesLoaded == 1);
        await queued.StartAsync();
        await next.StartAsync();
        await waitFor(() => governor.Waiting == 2);

        // Leaving the line hands its place to the agent behind it once the holder lets go
        await queued.Agent.StopAndDrainAsync(CancellationToken.None);
        governor.Waiting.ShouldBe(1);

        await holder.DisposeAsync();
        await waitFor(() => next.PagesLoaded == 1);
        queued.PagesLoaded.ShouldBe(0);
        governor.InFlight.ShouldBe(100);

        // Disposal doesn't wait for a load already under way; that load gives its page back when it finishes
        await next.DisposeAsync();
        await waitFor(() => governor.InFlight == 0);
        await queued.DisposeAsync();
    }

    [Fact]
    public async Task without_a_budget_tenant_agents_load_as_before()
    {
        var agents = Enumerable.Range(0, 3).Select(i => new TenantAgent(null, $"t{i}")).ToArray();

        foreach (var agent in agents) agent.HoldCommits();
        foreach (var agent in agents) await agent.StartAsync();

        // All at once, where a budget of one page would let in only one of them
        await waitFor(() => agents.All(x => x.PagesLoaded >= 1));

        foreach (var agent in agents) await agent.DisposeAsync();
    }

    private static async Task waitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.Add(Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was never met");
            await Task.Delay(10);
        }
    }

    // A real per-tenant SubscriptionAgent over a loader that always fills its page, and an execution that commits
    // what it is handed, either at once or when the test lets it
    private sealed class TenantAgent : IAsyncDisposable
    {
        public const long HighWater = 1000;

        private readonly ConcurrentQueue<string>? _order;
        private readonly ConcurrentQueue<long> _held = new();
        private volatile bool _holding;
        private int _pagesLoaded;

        public TenantAgent(TenantLoadGovernor? governor, string tenantId, ConcurrentQueue<string>? order = null)
        {
            TenantId = tenantId;
            _order = order;

            var runtime = Substitute.For<IDaemonRuntime>();
            runtime.TenantLoadGovernorFor(Arg.Any<ShardName>()).Returns(governor);
            Runtime = runtime;

            Agent = new SubscriptionAgent(new ShardName("Trips", ShardName.All, 1, tenantId),
                new AsyncOptions { BatchSize = 100 }, TimeProvider.System, new FullPageLoader(this),
                new CommittingExecution(this), new ShardStateTracker(NullLogger.Instance),
                Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);
        }

        public string TenantId { get; }
        public IDaemonRuntime Runtime { get; }
        public SubscriptionAgent Agent { get; }
        public int PagesLoaded => Volatile.Read(ref _pagesLoaded);

        public void HoldCommits() => _holding = true;

        public void CommitEverything()
        {
            _holding = false;
            while (_held.TryDequeue(out var ceiling))
            {
                _ = Agent.MarkSuccessAsync(ceiling).AsTask();
            }
        }

        public Task StartAsync()
        {
            return Agent.StartAsync(new SubscriptionExecutionRequest(0, ShardExecutionMode.Continuous,
                new ErrorHandlingOptions(), Runtime) { StartingHighWater = HighWater });
        }

        public async ValueTask DisposeAsync() => await Agent.DisposeAsync();

        private void loaded()
        {
            Interlocked.Increment(ref _pagesLoaded);
            _order?.Enqueue(TenantId);
        }

        private ValueTask handle(EventPage page, ISubscriptionAgent agent)
        {
            if (_holding)
            {
                _held.Enqueue(page.Ceiling);

                // Committed if the test let go between the check and the enqueue
                if (!_holding) CommitEverything();
                return ValueTask.CompletedTask;
            }

            return agent.MarkSuccessAsync(page.Ceiling);
        }

        private sealed class FullPageLoader(TenantAgent parent) : IEventLoader
        {
            public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
            {
                parent.loaded();

                var page = new EventPage(request.Floor);
                var target = Math.Min(request.Floor + request.BatchSize, request.HighWater);
                for (var sequence = request.Floor + 1; sequence <= target; sequence++)
                {
                    page.Add(new Event<AEvent>(new AEvent()) { Sequence = sequence });
                }

                page.CalculateCeiling(request.BatchSize, request.HighWater);
                return Task.FromResult(page);
            }
        }

        private sealed class CommittingExecution(TenantAgent parent) : ISubscriptionExecution
        {
            public ShardName ShardName => parent.Agent.Name;
            public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

            public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent) =>
                parent.handle(page, subscriptionAgent);

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

            public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
            {
                caching = null;
                return false;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
