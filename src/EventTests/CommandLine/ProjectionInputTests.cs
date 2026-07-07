using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

public class ProjectionInputTests
{
    [Fact]
    public void unset_max_concurrent_is_unbounded()
    {
        // jasperfx#420: no flag preserves the historical unbounded fan-out (-1 == unbounded for
        // ParallelOptions.MaxDegreeOfParallelism).
        new ProjectionInput().ResolveMaxDegreeOfParallelism().ShouldBe(-1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(64)]
    public void positive_max_concurrent_is_honored(int cap)
    {
        new ProjectionInput { MaxConcurrentFlag = cap }
            .ResolveMaxDegreeOfParallelism().ShouldBe(cap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    public void non_positive_max_concurrent_falls_back_to_unbounded(int cap)
    {
        // A nonsensical override must not deadlock the rebuild (MaxDegreeOfParallelism = 0 throws);
        // treat it as "unbounded".
        new ProjectionInput { MaxConcurrentFlag = cap }
            .ResolveMaxDegreeOfParallelism().ShouldBe(-1);
    }

    [Fact]
    public void configured_store_default_applies_when_no_flag()
    {
        // jasperfx#420: with no --max-concurrent flag, the store's configured
        // MaxConcurrentRebuildsPerDatabase default is honored.
        new ProjectionInput().ResolveMaxDegreeOfParallelism(configuredDefault: 12).ShouldBe(12);
    }

    [Fact]
    public void cli_flag_overrides_the_configured_store_default()
    {
        // A one-off operational --max-concurrent must win over the configured default.
        new ProjectionInput { MaxConcurrentFlag = 4 }
            .ResolveMaxDegreeOfParallelism(configuredDefault: 12).ShouldBe(4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void non_positive_configured_default_is_unbounded(int configured)
    {
        // A store that reports a nonsensical default must not deadlock the rebuild.
        new ProjectionInput().ResolveMaxDegreeOfParallelism(configured).ShouldBe(-1);
    }

    [Fact]
    public void no_flag_and_no_configured_default_stays_unbounded()
    {
        new ProjectionInput().ResolveMaxDegreeOfParallelism(configuredDefault: null).ShouldBe(-1);
    }
}
