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
}
