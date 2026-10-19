using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// jasperfx#407 Phase 2b: the base-daemon mechanism that drives per-tenant high-water and routes each
// tenant's mark to only that tenant's agents. Tested in isolation; the full daemon wiring is exercised
// downstream against a concrete store (marten#4596).
public class TenantedHighWaterCoordinatorTests
{
    private static HighWaterStatistics stat(string tenantId, long current, long highest, DateTimeOffset? timestamp = null)
        => new()
        {
            TenantId = tenantId, CurrentMark = current, HighestSequence = highest, LastMark = current,
            Timestamp = timestamp ?? default
        };

    private static ISubscriptionAgent agentFor(ShardName name)
    {
        var agent = Substitute.For<ISubscriptionAgent>();
        agent.Name.Returns(name);
        return agent;
    }

    [Fact]
    public void capability_flag_defaults_off_and_a_partitioned_store_opts_in()
    {
        // Non-partitioned detector: the daemon will NOT construct a coordinator (path unchanged).
        ((IHighWaterDetector)new GlobalOnlyDetector(1)).SupportsTenantPartitioning.ShouldBeFalse();
        // Partitioned detector opts in.
        ((IHighWaterDetector)new FakeVectorDetector()).SupportsTenantPartitioning.ShouldBeTrue();
    }

    [Fact]
    public void sync_assigned_tenants_derives_the_polled_set_from_running_shards()
    {
        var coordinator = new TenantedHighWaterCoordinator(new FakeVectorDetector());

        coordinator.SyncAssignedTenants([
            ShardName.Compose("Orders", "All", "t1"),
            ShardName.Compose("Payments", "All", "t1"), // same tenant, different projection -> deduped
            ShardName.Compose("Orders", "All", "t2"),
            new ShardName("GlobalProjection") // null tenant -> excluded, stays on the global agent
        ]);

        coordinator.PolledTenants.Snapshot().ShouldBe(["t1", "t2"], ignoreOrder: true);
    }

    [Fact]
    public async Task poll_and_route_pushes_each_tenants_mark_to_only_that_tenants_agents()
    {
        var detector = new FakeVectorDetector();
        detector.Enqueue(new HighWaterVector([stat("t1", 10, 10), stat("t2", 20, 20)]));

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1", "t2"]);

        var agentT1 = agentFor(ShardName.Compose("Orders", "All", "t1"));
        var agentT2 = agentFor(ShardName.Compose("Orders", "All", "t2"));
        var agentGlobal = agentFor(new ShardName("Orders"));

        await coordinator.PollAndRouteAsync([agentT1, agentT2, agentGlobal], CancellationToken.None);

        agentT1.Received(1).MarkHighWater(10);
        agentT2.Received(1).MarkHighWater(20);
        // tenant t1's mark never reaches t2 (and vice versa), and the global agent is untouched
        agentT1.DidNotReceive().MarkHighWater(20);
        agentT2.DidNotReceive().MarkHighWater(10);
        agentGlobal.DidNotReceive().MarkHighWater(Arg.Any<long>());
    }

    [Fact]
    public async Task heterogeneous_tenant_states_are_detected_and_routed_independently()
    {
        var detector = new FakeVectorDetector();
        detector.Enqueue(new HighWaterVector([stat("stale", 10, 10), stat("flat", 5, 5), stat("advancing", 3, 3)]));
        detector.Enqueue(new HighWaterVector([stat("stale", 10, 25), stat("flat", 5, 5), stat("advancing", 8, 12)]));

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["stale", "flat", "advancing"]);

        var agents = new[]
        {
            agentFor(ShardName.Compose("P", "All", "stale")),
            agentFor(ShardName.Compose("P", "All", "flat")),
            agentFor(ShardName.Compose("P", "All", "advancing"))
        };

        await coordinator.PollAndRouteAsync(agents, CancellationToken.None); // baseline
        var readings = (await coordinator.PollAndRouteAsync(agents, CancellationToken.None))
            .ToDictionary(x => x.TenantId);

        readings["stale"].Status.ShouldBe(HighWaterStatus.Stale);
        readings["flat"].Status.ShouldBe(HighWaterStatus.CaughtUp);
        readings["advancing"].Status.ShouldBe(HighWaterStatus.Changed);

        // The advancing tenant's agent still got its own mark even though another tenant went stale
        agents[2].Received().MarkHighWater(8);
    }

    [Fact]
    public async Task poll_and_route_persists_each_tenants_mark_with_its_timestamp()
    {
        // jasperfx#449: the per-tenant high-water row must carry the per-tenant timestamp so a monitor
        // reading AllProjectionProgress gets per-tenant staleness, not a store-global heuristic.
        var t1At = new DateTimeOffset(2026, 6, 13, 1, 0, 0, TimeSpan.Zero);
        var t2At = new DateTimeOffset(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);

        var detector = new FakeVectorDetector();
        detector.Enqueue(new HighWaterVector([stat("t1", 10, 10, t1At), stat("t2", 20, 20, t2At)]));

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1", "t2"]);

        await coordinator.PollAndRouteAsync(
            [agentFor(ShardName.Compose("Orders", "All", "t1")), agentFor(ShardName.Compose("Orders", "All", "t2"))],
            CancellationToken.None);

        detector.PersistedTenantMarks.ShouldBe(
            [("t1", 10L, t1At), ("t2", 20L, t2At)], ignoreOrder: true);
    }

    [Fact]
    public async Task ceiling_for_returns_the_tenants_rebuild_ceiling()
    {
        var detector = new FakeVectorDetector();
        detector.Enqueue(new HighWaterVector([stat("t1", 42, 50)]));

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1"]);

        await coordinator.PollAndRouteAsync([agentFor(ShardName.Compose("P", "All", "t1"))], CancellationToken.None);

        coordinator.CeilingFor("t1").ShouldBe(42);
        coordinator.CeilingFor("never-polled").ShouldBeNull();
    }

    [Fact]
    public async Task tracks_a_liveness_heartbeat_across_polls()
    {
        // jasperfx#539: the per-tenant path is the daemon's Path B. Each completed vectorized poll stamps a
        // liveness heartbeat so the daemon can tell "no new events" from "the tenant high-water poll died".
        var detector = new FakeVectorDetector();
        detector.Enqueue(new HighWaterVector([stat("t1", 10, 10)]));

        var coordinator = new TenantedHighWaterCoordinator(detector);
        coordinator.PolledTenants.SetTenants(["t1"]);

        // No cycle has completed yet: no heartbeat, never stale.
        coordinator.LastPolledAt.ShouldBeNull();
        coordinator.IsStale(TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow).ShouldBeFalse();

        await coordinator.PollAndRouteAsync(
            [agentFor(ShardName.Compose("Orders", "All", "t1"))], CancellationToken.None);

        coordinator.LastPolledAt.ShouldNotBeNull();
        // Fresh poll is not stale against a generous threshold...
        coordinator.IsStale(TimeSpan.FromHours(1), DateTimeOffset.UtcNow).ShouldBeFalse();
        // ...but is once "now" has advanced well past the last poll.
        coordinator.IsStale(TimeSpan.FromSeconds(1), coordinator.LastPolledAt!.Value.AddSeconds(5))
            .ShouldBeTrue();
    }

    // Marten's RemoveTenantFromShardAsync drops a tenant's events and its per-tenant sequence, and a later
    // AddTenantToShardAsync gives it a fresh sequence that starts again at 1 ("a re-added tenant starts
    // fresh", marten#4880). In between, the coordinator's reconciliation reaps the tenant's agent, so the
    // tenant leaves the polled set. When it comes back, the mark its removed incarnation reached says nothing
    // about its new events. This drives a coordinator to the moment the re-added tenant's new agent is primed:
    // its old incarnation reached mark 2, and none of its new events have committed yet.
    private static async Task<(TenantedHighWaterCoordinator Coordinator, FakeVectorDetector Detector,
            Dictionary<string, long> Marks, ShardName T1, ShardName T2)>
        reAddedTenantBeforeItsFirstEventAsync()
    {
        var marks = new Dictionary<string, long> { ["t1"] = 3, ["t2"] = 2 };
        var detector = new FakeVectorDetector();
        detector.EnqueueFactory(tenants => new HighWaterVector(tenants.Select(t => stat(t, marks[t], marks[t]))));

        var coordinator = new TenantedHighWaterCoordinator(detector);
        var t1 = ShardName.Compose("Orders", "All", "t1");
        var t2 = ShardName.Compose("Orders", "All", "t2");

        coordinator.SyncAssignedTenants([t1, t2]);
        await coordinator.PollAndRouteAsync([agentFor(t1), agentFor(t2)], CancellationToken.None);
        coordinator.CeilingFor("t2").ShouldBe(2);

        // t2 is removed: its agent is reaped, so it is no longer polled on this node...
        coordinator.SyncAssignedTenants([t1]);

        // ...and re-added, with a new sequence and no events yet. Its new agent's start pins it and primes
        // its ceiling, exactly as JasperFxAsyncDaemon.StartAgentAsync does for a tenant-bearing identity.
        marks["t2"] = 0;
        coordinator.PolledTenants.Pin("t2");
        await coordinator.PollAndRouteAsync([agentFor(t1)], CancellationToken.None);

        return (coordinator, detector, marks, t1, t2);
    }

    [Fact]
    public async Task a_re_added_tenant_is_not_answered_with_the_ceiling_of_its_removed_incarnation()
    {
        // The new agent is seeded from CeilingFor. Answering the old incarnation's 2 makes it consume (0, 2]
        // as an empty range before any of the new events exist, and then skip them for good.
        var (coordinator, _, _, _, _) = await reAddedTenantBeforeItsFirstEventAsync();

        coordinator.CeilingFor("t2").ShouldBe(0);
    }

    [Fact]
    public async Task a_re_added_tenants_marks_are_routed_and_persisted_even_below_its_removed_incarnations_mark()
    {
        var (coordinator, detector, marks, t1, t2) = await reAddedTenantBeforeItsFirstEventAsync();

        // The new agent registers, and the re-added tenant's first two events commit: mark 2 again, in its
        // new sequence.
        var reAdded = agentFor(t2);
        coordinator.SyncAssignedTenants([t1, t2]);
        coordinator.PolledTenants.Unpin("t2");
        marks["t2"] = 2;

        await coordinator.PollAndRouteAsync([agentFor(t1), reAdded], CancellationToken.None);

        coordinator.ShouldSatisfyAllConditions(
            // Not routed, a new agent seeded at 0 would wait for a third event before reading the first two.
            () => reAdded.Received(1).MarkHighWater(2),
            // The removal deleted the tenant's high-water row, so the new incarnation's mark is written again.
            () => detector.PersistedTenantMarks.Count(x => x is { TenantId: "t2", Sequence: 2 }).ShouldBe(2));
    }

    [Fact]
    public async Task a_reading_of_a_tenant_that_rejoined_mid_cycle_is_neither_routed_nor_persisted()
    {
        // A cycle awaits each tenant's high-water write in turn, so it can outlast a tenant's removal, the reap
        // of its agents and its re-add. A reading taken before all that may describe the incarnation that left:
        // routed, it would move the new agent past events it has not read, and recorded, it would hold the new
        // incarnation's marks back.
        var detector = new GatedPersistDetector(new HighWaterVector([stat("t1", 5, 5), stat("t2", 9, 9)]));
        var coordinator = new TenantedHighWaterCoordinator(detector);
        var shards = new Dictionary<string, ShardName>
        {
            ["t1"] = ShardName.Compose("Orders", "All", "t1"), ["t2"] = ShardName.Compose("Orders", "All", "t2")
        };
        var agents = shards.ToDictionary(x => x.Key, x => agentFor(x.Value));
        coordinator.SyncAssignedTenants(shards.Values);

        var cycle = coordinator.PollAndRouteAsync(agents.Values.ToArray(), CancellationToken.None);

        // The cycle is writing the first tenant's mark. Meanwhile the other tenant's agent is reaped, and the
        // tenant is re-added and its new agent's start pins it.
        var first = await detector.FirstPersist.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        var other = first == "t1" ? "t2" : "t1";
        coordinator.SyncAssignedTenants([shards[first]]);
        coordinator.PolledTenants.Pin(other);

        detector.ReleasePersists();
        await cycle;

        coordinator.ShouldSatisfyAllConditions(
            () => agents[other].DidNotReceive().MarkHighWater(Arg.Any<long>()),
            () => detector.PersistedTenants.ShouldBe([first]));
    }
}

// A partitioned detector whose per-tenant high-water writes block until released, so a test can change the
// polled set while a cycle is part-way through its readings.
internal class GatedPersistDetector : IHighWaterDetector
{
    private readonly HighWaterVector _vector;
    private readonly TaskCompletionSource<string> _firstPersist =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _persisted = new();

    public GatedPersistDetector(HighWaterVector vector) => _vector = vector;

    public Uri DatabaseUri { get; } = new("fake://db");

    public bool SupportsTenantPartitioning => true;

    public Task<string> FirstPersist => _firstPersist.Task;

    public IReadOnlyList<string> PersistedTenants
    {
        get
        {
            lock (_persisted)
            {
                return _persisted.ToList();
            }
        }
    }

    public void ReleasePersists() => _release.TrySetResult();

    public async Task MarkHighWaterForTenantAsync(string tenantId, long sequence, DateTimeOffset timestamp,
        CancellationToken token)
    {
        lock (_persisted)
        {
            _persisted.Add(tenantId);
        }

        _firstPersist.TrySetResult(tenantId);
        await _release.Task;
    }

    public Task<HighWaterStatistics> Detect(CancellationToken token) => Task.FromResult(new HighWaterStatistics());

    public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

    public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken token)
        => Task.FromResult(_vector);

    public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
        CancellationToken token)
        => DetectForTenantsAsync(tenantIds, token);
}
