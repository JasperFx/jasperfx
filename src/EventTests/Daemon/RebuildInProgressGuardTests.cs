using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;
using RebuildHarness = EventTests.Daemon.CrossProductRebuildCapTests.RebuildHarness;

namespace EventTests.Daemon;

// jasperfx#1009: while a rebuild replays, its agent is not registered with the daemon, so nothing stopped a
// continuous start (Wolverine's node agent, the projection coordinator, a CritterWatch retry) from running a
// second agent over the same progression row. These drive the real daemon through the gated loader of
// CrossProductRebuildCapTests, so "while the rebuild is replaying" is a held gate rather than a timing guess.
public class RebuildInProgressGuardTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static ShardName tenantShard(string projection, string tenantId)
        => new ShardName(projection).ForTenant(tenantId);

    [Fact]
    public async Task a_start_requested_while_the_rebuild_replays_is_refused_as_rebuilding()
    {
        await using var harness = new RebuildHarness(["t1", "t2"], ["ProjA"]);

        var rebuild = harness.Daemon.RebuildProjectionAsync("ProjA", "t1", TestTimeout, CancellationToken.None);
        var replaying = await harness.Loader.NextEntryAsync();

        harness.Daemon.IsRebuilding(tenantShard("ProjA", "t1")).ShouldBeTrue();

        var ex = await Should.ThrowAsync<ShardStartException>(() =>
            harness.Daemon.StartAgentAsync(tenantShard("ProjA", "t1"), CancellationToken.None));

        ex.Reason.ShouldBe(ShardStartFailureReason.Rebuilding);
        ex.IsTransient.ShouldBeTrue();

        // No continuous agent appeared beside the replay for that progression row
        harness.Daemon.CurrentAgents().ShouldNotContain(x => x.Name.Identity == "ProjA:All:t1");

        replaying.SetResult();
        await rebuild.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        harness.Daemon.IsRebuilding(tenantShard("ProjA", "t1")).ShouldBeFalse();
    }

    [Fact]
    public async Task a_tenant_rebuild_only_blocks_that_tenants_shards()
    {
        await using var harness = new RebuildHarness(["t1", "t2"], ["ProjA", "ProjB"]);

        var rebuild = harness.Daemon.RebuildProjectionAsync("ProjA", "t1", TestTimeout, CancellationToken.None);
        var replaying = await harness.Loader.NextEntryAsync();

        harness.Daemon.IsRebuilding(tenantShard("ProjA", "t1")).ShouldBeTrue();
        harness.Daemon.IsRebuilding(tenantShard("ProjA", "t2")).ShouldBeFalse();
        harness.Daemon.IsRebuilding(tenantShard("ProjB", "t1")).ShouldBeFalse();

        // Another tenant of the same projection can still be rebuilt alongside it
        var other = harness.Daemon.RebuildProjectionAsync("ProjA", "t2", TestTimeout, CancellationToken.None);
        (await harness.Loader.NextEntryAsync()).SetResult();
        await other.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        replaying.SetResult();
        await rebuild.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_second_rebuild_of_the_same_projection_fails_fast_and_the_first_completes()
    {
        await using var harness = new RebuildHarness(["t1"], ["ProjA"]);

        // A rebuild of every tenant: it claims the projection for all of them
        var first = harness.Daemon.RebuildProjectionAsync("ProjA", tenantId: null, TestTimeout, CancellationToken.None);
        var replaying = await harness.Loader.NextEntryAsync();

        var again = await Should.ThrowAsync<ProjectionAlreadyRebuildingException>(() =>
            harness.Daemon.RebuildProjectionAsync("ProjA", tenantId: null, TestTimeout, CancellationToken.None));
        again.ProjectionName.ShouldBe("ProjA");
        again.TenantId.ShouldBeNull();

        var oneTenant = await Should.ThrowAsync<ProjectionAlreadyRebuildingException>(() =>
            harness.Daemon.RebuildProjectionAsync("ProjA", "t1", TestTimeout, CancellationToken.None));
        oneTenant.TenantId.ShouldBe("t1");

        // The store-global overloads take the same claim
        await Should.ThrowAsync<ProjectionAlreadyRebuildingException>(() =>
            harness.Daemon.RebuildProjectionAsync("ProjA", TestTimeout, CancellationToken.None));

        var start = await Should.ThrowAsync<ShardStartException>(() =>
            harness.Daemon.StartAgentAsync(tenantShard("ProjA", "t1"), CancellationToken.None));
        start.Reason.ShouldBe(ShardStartFailureReason.Rebuilding);

        replaying.SetResult();
        await first.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        // The refused attempts never reached the loader: the one cell was replayed exactly once
        harness.Loader.CellsLoaded.ShouldBe(["ProjA:All:t1"]);
        harness.Daemon.IsRebuilding(tenantShard("ProjA", "t1")).ShouldBeFalse();
    }

    [Fact]
    public async Task the_guard_is_released_after_a_rebuild_that_throws()
    {
        await using var harness = new RebuildHarness(["t1"], ["ProjA"], gated: false);

        harness.Database.EnsureStorageExistsAsync(typeof(IEvent), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new DivideByZeroException()));

        await Should.ThrowAsync<DivideByZeroException>(() =>
            harness.Daemon.RebuildProjectionAsync("ProjA", "t1", TestTimeout, CancellationToken.None));
        await Should.ThrowAsync<DivideByZeroException>(() =>
            harness.Daemon.RebuildProjectionAsync("ProjA", tenantId: null, TestTimeout, CancellationToken.None));

        harness.Daemon.IsRebuilding(tenantShard("ProjA", "t1")).ShouldBeFalse();

        harness.Database.EnsureStorageExistsAsync(typeof(IEvent), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Neither failed rebuild left a claim behind to refuse this one
        await harness.Daemon.RebuildProjectionAsync("ProjA", "t1", TestTimeout, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        harness.Loader.CellsLoaded.ShouldBe(["ProjA:All:t1"]);
    }
}
