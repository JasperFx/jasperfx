using JasperFx.Events.Daemon;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#407 Phase 2: cross-tenant "rebuild X everywhere" fan-out abstraction.
public class CrossTenantRebuildTests
{
    [Fact]
    public async Task fans_out_one_per_tenant_rebuild_for_each_target_tenant()
    {
        var source = Substitute.For<ICrossTenantRebuildSource>();
        source.FindRebuildTenantsAsync("Orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["acme", "globex", "initech"]));

        var daemon = Substitute.For<IProjectionDaemon>();
        var timeout = TimeSpan.FromMinutes(5);

        var rebuilt = await new CrossTenantRebuild(source)
            .RebuildEverywhereAsync(daemon, "Orders", timeout, CancellationToken.None);

        rebuilt.ShouldBe(["acme", "globex", "initech"]);

        await daemon.Received(1).RebuildProjectionAsync("Orders", "acme", timeout, Arg.Any<CancellationToken>());
        await daemon.Received(1).RebuildProjectionAsync("Orders", "globex", timeout, Arg.Any<CancellationToken>());
        await daemon.Received(1).RebuildProjectionAsync("Orders", "initech", timeout, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task does_nothing_when_no_tenants_carry_the_projection()
    {
        var source = Substitute.For<ICrossTenantRebuildSource>();
        source.FindRebuildTenantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var daemon = Substitute.For<IProjectionDaemon>();

        var rebuilt = await new CrossTenantRebuild(source)
            .RebuildEverywhereAsync(daemon, "Orders", TimeSpan.FromMinutes(5), CancellationToken.None);

        rebuilt.ShouldBeEmpty();
        await daemon.DidNotReceive()
            .RebuildProjectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task honors_a_max_parallelism_bound()
    {
        var source = Substitute.For<ICrossTenantRebuildSource>();
        source.FindRebuildTenantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["t1", "t2", "t3", "t4", "t5"]));

        var daemon = Substitute.For<IProjectionDaemon>();

        var rebuilt = await new CrossTenantRebuild(source)
            .RebuildEverywhereAsync(daemon, "Orders", TimeSpan.FromMinutes(5), CancellationToken.None, maxParallelism: 2);

        rebuilt.Count.ShouldBe(5);
        await daemon.Received(5)
            .RebuildProjectionAsync("Orders", Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
