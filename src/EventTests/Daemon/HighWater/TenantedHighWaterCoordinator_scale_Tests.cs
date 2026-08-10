using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// jasperfx#644: at 2,173 tenants × ~4,400 agents the per-tenant high-water path OOM'd the host. Three
// compounding causes, each pinned here through observable seams rather than allocation assertions:
//   1. routing was O(tenants × agents) name comparisons per cycle,
//   2. every cycle re-routed and re-persisted every tenant even when no mark moved (thousands of queued
//      agent commands + sequential database writes per cycle, forever), and
//   3. nothing bounded concurrent cycles — the unguarded OnNext trigger and the watchdog's
//      "abandon the hung poll" both stacked live full cycles without limit.
// The fix: group agents by tenant, route/persist only advanced marks, retire a superseded in-flight
// cycle via a cycle epoch, and coalesce background triggers into a single-flight cycle with at most one
// trailing rerun.
public class TenantedHighWaterCoordinator_scale_Tests
{
    private static HighWaterStatistics stat(string tenantId, long mark)
        => new() { TenantId = tenantId, CurrentMark = mark, LastMark = mark, HighestSequence = mark + 1 };

    [Fact]
    public async Task unchanged_tenants_are_not_rerouted_and_not_repersisted()
    {
        var detector = new ScaleDetector(tenants => new HighWaterVector(tenants.Select(t => stat(t, 10))));
        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1", "t2"]);

        var agentT1 = new RecordingAgent(ShardName.Compose("Orders", "All", "t1"));
        var agentT2 = new RecordingAgent(ShardName.Compose("Orders", "All", "t2"));
        var agents = new ISubscriptionAgent[] { agentT1, agentT2 };

        await coordinator.PollAndRouteAsync(agents, CancellationToken.None);
        await coordinator.PollAndRouteAsync(agents, CancellationToken.None);
        await coordinator.PollAndRouteAsync(agents, CancellationToken.None);

        // First cycle routed and persisted each tenant once; the two flat follow-up cycles did neither.
        agentT1.Marks.ShouldBe([10L]);
        agentT2.Marks.ShouldBe([10L]);
        detector.Persisted.Count(x => x.TenantId == "t1").ShouldBe(1);
        detector.Persisted.Count(x => x.TenantId == "t2").ShouldBe(1);

        // The readings themselves still come back every cycle (observability contract unchanged) and the
        // liveness heartbeat still stamps flat cycles.
        coordinator.LastPolledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task an_advanced_tenant_is_rerouted_and_repersisted()
    {
        var marks = new Dictionary<string, long> { ["t1"] = 10, ["t2"] = 20 };
        var detector = new ScaleDetector(tenants => new HighWaterVector(tenants.Select(t => stat(t, marks[t]))));
        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1", "t2"]);

        var agentT1 = new RecordingAgent(ShardName.Compose("Orders", "All", "t1"));
        var agentT2 = new RecordingAgent(ShardName.Compose("Orders", "All", "t2"));
        var agents = new ISubscriptionAgent[] { agentT1, agentT2 };

        await coordinator.PollAndRouteAsync(agents, CancellationToken.None);

        marks["t1"] = 15; // only t1 advances
        await coordinator.PollAndRouteAsync(agents, CancellationToken.None);

        agentT1.Marks.ShouldBe([10L, 15L]);
        agentT2.Marks.ShouldBe([20L]);
        detector.Persisted.Where(x => x.TenantId == "t1").Select(x => x.Sequence).ShouldBe([10L, 15L]);
        detector.Persisted.Where(x => x.TenantId == "t2").Select(x => x.Sequence).ShouldBe([20L]);
    }

    [Fact]
    public async Task at_thousands_of_tenants_a_cycle_only_works_the_tenants_that_advanced()
    {
        const int tenantCount = 3000;
        const int agentsPerTenant = 2;

        var marks = new Dictionary<string, long>();
        var tenants = new List<string>(tenantCount);
        var agents = new List<ISubscriptionAgent>(tenantCount * agentsPerTenant);
        var agentsByTenant = new Dictionary<string, List<RecordingAgent>>();

        for (var i = 0; i < tenantCount; i++)
        {
            var tenantId = $"tenant{i:D5}";
            tenants.Add(tenantId);
            marks[tenantId] = 100;

            var bucket = new List<RecordingAgent>();
            for (var j = 0; j < agentsPerTenant; j++)
            {
                var agent = new RecordingAgent(ShardName.Compose($"Projection{j}", "All", tenantId));
                bucket.Add(agent);
                agents.Add(agent);
            }

            agentsByTenant[tenantId] = bucket;
        }

        var detector = new ScaleDetector(polled => new HighWaterVector(polled.Select(t => stat(t, marks[t]))));
        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(tenants);

        // Cycle 1: everything is new, so every tenant is routed to exactly its own agents and persisted once.
        await coordinator.PollAndRouteAsync(agents, CancellationToken.None);
        agents.Cast<RecordingAgent>().ShouldAllBe(x => x.Marks.Count == 1 && x.Marks[0] == 100L);
        detector.Persisted.Count.ShouldBe(tenantCount);

        // Cycle 2: only 25 tenants advance. The cycle's routed/persisted work must be proportional to the
        // 25 advanced tenants, NOT to tenants × agents — this is the gh-644 steady-state shape.
        var advanced = tenants.Where((_, i) => i % 120 == 0).Take(25).ToArray();
        advanced.Length.ShouldBe(25);
        foreach (var tenantId in advanced)
        {
            marks[tenantId] = 250;
        }

        detector.Persisted.Clear();
        await coordinator.PollAndRouteAsync(agents, CancellationToken.None);

        detector.Persisted.Select(x => x.TenantId).OrderBy(x => x).ShouldBe(advanced.OrderBy(x => x));
        detector.Persisted.ShouldAllBe(x => x.Sequence == 250L);

        var totalRoutedInCycle2 = agents.Cast<RecordingAgent>().Sum(x => x.Marks.Count) -
                                  tenantCount * agentsPerTenant;
        totalRoutedInCycle2.ShouldBe(advanced.Length * agentsPerTenant);
        foreach (var tenantId in advanced)
        {
            agentsByTenant[tenantId].ShouldAllBe(x => x.Marks.SequenceEqual(new[] { 100L, 250L }));
        }
    }

    [Fact]
    public async Task a_newer_cycle_retires_an_in_flight_cycle_without_losing_tenants()
    {
        // Cycle A parks inside its first per-tenant persist; cycle B starts and runs to completion while A
        // is parked. When A resumes it must retire itself at the next reading instead of grinding through
        // the remaining tenants — that leaked "abandoned but still running" cycle, once per watchdog
        // window, was the gh-644 OOM. B covers everything A never reached, so the hand-off is gapless.
        var detector = new ScaleDetector(polled =>
            new HighWaterVector(polled.OrderBy(x => x).Select(t => stat(t, 10))));

        var firstPersistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateArmed = 1;
        detector.PersistGate = async () =>
        {
            if (Interlocked.CompareExchange(ref gateArmed, 0, 1) == 1)
            {
                firstPersistEntered.SetResult();
                await releaseFirstPersist.Task;
            }
        };

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1", "t2", "t3"]);

        var agents = new ISubscriptionAgent[]
        {
            new RecordingAgent(ShardName.Compose("P", "All", "t1")),
            new RecordingAgent(ShardName.Compose("P", "All", "t2")),
            new RecordingAgent(ShardName.Compose("P", "All", "t3"))
        };

        var cycleA = coordinator.PollAndRouteAsync(agents, CancellationToken.None);
        await firstPersistEntered.Task; // A is parked persisting its first tenant

        await coordinator.PollAndRouteAsync(agents, CancellationToken.None); // B runs to completion

        releaseFirstPersist.SetResult();
        await cycleA;

        // A persisted exactly the one tenant it was parked on; B persisted all three (A had recorded no
        // successful persists when B ran). A never touched its remaining two tenants.
        detector.Persisted.Count.ShouldBe(4);
        detector.Persisted.Select(x => x.TenantId).Distinct().Count().ShouldBe(3);

        // Every agent still saw its tenant's mark at least once — supersession loses nothing.
        agents.Cast<RecordingAgent>().ShouldAllBe(x => x.Marks.Contains(10L));
    }

    [Fact]
    public async Task coalesced_polls_are_single_flight_with_one_trailing_rerun()
    {
        // Simulates the daemon's background triggers (OnNext fast path + cadence timer) stacking up while
        // one slow cycle runs: every trigger beyond the first must coalesce into at most ONE trailing
        // rerun, never a concurrent cycle each. The unguarded fire-and-forget version of this is exactly
        // how cycles piled up to the gh-644 OOM.
        var detectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDetect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDetect = 1;

        var detector = new ScaleDetector(polled => new HighWaterVector(polled.Select(t => stat(t, 10))))
        {
            DetectGate = async () =>
            {
                if (Interlocked.CompareExchange(ref firstDetect, 0, 1) == 1)
                {
                    detectEntered.SetResult();
                    await releaseDetect.Task;
                }
            }
        };

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1"]);

        IReadOnlyList<ISubscriptionAgent> agentsSource() => [new RecordingAgent(ShardName.Compose("P", "All", "t1"))];

        var owner = coordinator.PollAndRouteCoalescedAsync(agentsSource, CancellationToken.None);
        await detectEntered.Task; // the owning cycle is now parked mid-poll

        // A burst of background triggers lands while the owner is in flight — every one must coalesce.
        for (var i = 0; i < 50; i++)
        {
            (await coordinator.PollAndRouteCoalescedAsync(agentsSource, CancellationToken.None))
                .ShouldBeFalse();
        }

        detector.PollCount.ShouldBe(1); // nothing ran concurrently with the parked owner

        releaseDetect.SetResult();
        (await owner).ShouldBeTrue();

        // The 50 triggers collapsed into exactly one trailing rerun on the owner: 2 cycles total, and at
        // no point did two detections overlap.
        detector.PollCount.ShouldBe(2);
        detector.MaxConcurrentPolls.ShouldBe(1);
    }

    [Fact]
    public async Task abandon_in_flight_poll_lets_a_fresh_cycle_start_and_retires_the_wedged_one()
    {
        // The daemon watchdog's restart seam: a cycle wedged in the detector holds the single-flight
        // guard. AbandonInFlightPoll must release the guard so a fresh coalesced cycle can run, and the
        // wedged cycle must retire itself when it finally wakes.
        var detectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDetect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDetect = 1;

        var detector = new ScaleDetector(polled => new HighWaterVector(polled.Select(t => stat(t, 10))))
        {
            DetectGate = async () =>
            {
                if (Interlocked.CompareExchange(ref firstDetect, 0, 1) == 1)
                {
                    detectEntered.SetResult();
                    await releaseDetect.Task;
                }
            }
        };

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1"]);

        IReadOnlyList<ISubscriptionAgent> agentsSource() => [new RecordingAgent(ShardName.Compose("P", "All", "t1"))];

        var wedged = coordinator.PollAndRouteCoalescedAsync(agentsSource, CancellationToken.None);
        await detectEntered.Task;

        // While the guard is held, triggers coalesce...
        (await coordinator.PollAndRouteCoalescedAsync(agentsSource, CancellationToken.None)).ShouldBeFalse();

        // ...until the watchdog abandons the wedged cycle. A fresh cycle must then run for real.
        coordinator.AbandonInFlightPoll();
        (await coordinator.PollAndRouteCoalescedAsync(agentsSource, CancellationToken.None)).ShouldBeTrue();
        detector.Persisted.Count.ShouldBe(1); // the fresh cycle did the work

        // The wedged cycle wakes, sees it was superseded, and does no per-tenant work of its own.
        releaseDetect.SetResult();
        await wedged;
        detector.Persisted.Count.ShouldBe(1);
    }

    // A deliberately hand-rolled agent double: NSubstitute proxies are too heavy at 6,000 instances, and
    // the only seam this suite observes is MarkHighWater.
    private sealed class RecordingAgent : ISubscriptionAgent
    {
        public RecordingAgent(ShardName name) => Name = name;

        public List<long> Marks { get; } = new();

        public ShardName Name { get; }
        public long Position => 0;
        public AgentStatus Status => AgentStatus.Running;
        public DateTimeOffset? PausedTime => null;
        public ISubscriptionMetrics Metrics => null!;
        public ShardExecutionMode Mode => ShardExecutionMode.Continuous;
        public ErrorHandlingOptions ErrorOptions => new();
        public AsyncOptions Options => new();

        public void MarkHighWater(long sequence)
        {
            lock (Marks)
            {
                Marks.Add(sequence);
            }
        }

        public ValueTask MarkSuccessAsync(long processedCeiling) => default;
        public Task ReportCriticalFailureAsync(Exception ex) => Task.CompletedTask;
        public Task ReportCriticalFailureAsync(Exception ex, long lastProcessed) => Task.CompletedTask;
        public Task RecordDeadLetterEventAsync(IEvent @event, Exception ex) => Task.CompletedTask;
        public Task RecordDeadLetterEventAsync(DeadLetterEvent @event) => Task.CompletedTask;
        public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;
        public Task HardStopAsync() => Task.CompletedTask;
        public Task StartAsync(SubscriptionExecutionRequest request) => Task.CompletedTask;

        public Task ReplayAsync(SubscriptionExecutionRequest request, long highWaterMark, TimeSpan timeout)
            => Task.CompletedTask;

        public void MarkSkipped(long sequence)
        {
        }
    }

    // A partitioned detector whose vector is computed from the polled set, with optional async gates so a
    // test can park a cycle mid-detect or mid-persist and observe concurrency.
    private sealed class ScaleDetector : IHighWaterDetector
    {
        private readonly Func<IReadOnlyCollection<string>, HighWaterVector> _factory;
        private int _concurrentPolls;
        private int _maxConcurrentPolls;
        private int _pollCount;

        public ScaleDetector(Func<IReadOnlyCollection<string>, HighWaterVector> factory) => _factory = factory;

        public Func<Task>? DetectGate { get; set; }
        public Func<Task>? PersistGate { get; set; }

        public int PollCount => Volatile.Read(ref _pollCount);
        public int MaxConcurrentPolls => Volatile.Read(ref _maxConcurrentPolls);

        public ConcurrentQueue<(string TenantId, long Sequence, DateTimeOffset Timestamp)> Persisted { get; } = new();

        public Uri DatabaseUri { get; } = new("fake://db");
        public bool SupportsTenantPartitioning => true;

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public async Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
        {
            Interlocked.Increment(ref _pollCount);
            var live = Interlocked.Increment(ref _concurrentPolls);
            try
            {
                InterlockedMax(ref _maxConcurrentPolls, live);
                if (DetectGate != null)
                {
                    await DetectGate();
                }

                return _factory(tenantIds);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentPolls);
            }
        }

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
            => DetectForTenantsAsync(tenantIds, token);

        public async Task MarkHighWaterForTenantAsync(string tenantId, long sequence, DateTimeOffset timestamp,
            CancellationToken token)
        {
            if (PersistGate != null)
            {
                await PersistGate();
            }

            Persisted.Enqueue((tenantId, sequence, timestamp));
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref location)))
            {
                if (Interlocked.CompareExchange(ref location, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
