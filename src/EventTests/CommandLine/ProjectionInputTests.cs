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
}
