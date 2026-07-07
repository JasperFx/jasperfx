using JasperFx.Events.Daemon;
using Shouldly;

namespace EventTests.Daemon;

// Epic #486 WS3: write-side throttling defaults.
public class BatchWriteGovernorDefaultsTests
{
    [Fact]
    public void daemon_settings_default_is_four()
    {
        new DaemonSettings().MaxConcurrentBatchWritesPerDatabase.ShouldBe(4);
    }

    [Fact]
    public void rebuild_everywhere_default_parallelism_follows_the_daemon_budget_then_four()
    {
        // #496: an unbounded default let a 100-tenant store fan out 100 concurrent rebuilds against
        // one database, so the default became a bounded 4. jasperfx#497 refines it: the declared
        // default is now null = "follow the daemon's shared per-database rebuild budget", and only
        // when the daemon reports no budget does the resolution fall back to the #496 value of 4
        // (0 still opts back into unbounded).
        var method = typeof(CrossTenantRebuild).GetMethod(nameof(CrossTenantRebuild.RebuildEverywhereAsync))!;
        method.GetParameters().Single(x => x.Name == "maxParallelism").DefaultValue.ShouldBeNull();

        CrossTenantRebuild.ResolveLaunchWidth(null, null).ShouldBe(4);
    }

    [Fact]
    public void agents_and_runtimes_default_to_no_governor()
    {
        // The default interface members keep every existing ISubscriptionAgent /
        // IDaemonRuntime implementation (including the Nullo) on unbounded behavior.
        (new NulloDaemonRuntime() as IDaemonRuntime).BatchWriteThrottle.ShouldBeNull();
    }
}
