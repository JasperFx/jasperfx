using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

// jasperfx#420 acceptance: the per-database rebuild fan-out must never run more than the configured
// cap of rebuild cells concurrently. We exercise the extracted ProjectionHost.RebuildProjectionsWithCapAsync
// helper directly with an Interlocked peak-concurrency counter (the same instrumentation the issue calls
// for) so the cap is verified without standing up a real event store / daemon.
public class RebuildConcurrencyCapTests
{
    private static async Task<int> RunAndMeasurePeak(int cellCount, int cap)
    {
        var names = Enumerable.Range(0, cellCount).Select(i => $"cell-{i}").ToArray();

        var current = 0;
        var peak = 0;
        var sync = new object();

        await ProjectionHost.RebuildProjectionsWithCapAsync(names, cap, CancellationToken.None,
            async (_, ct) =>
            {
                var now = Interlocked.Increment(ref current);
                lock (sync)
                {
                    if (now > peak) peak = now;
                }

                // Hold the slot long enough that, were the cap not enforced, additional cells would
                // pile in and push the observed peak above the cap.
                await Task.Delay(15, ct);

                Interlocked.Decrement(ref current);
            });

        return peak;
    }

    [Theory]
    [InlineData(256, 4)]   // the issue's scenario: 8 projections x 32 tenants, cap 4
    [InlineData(64, 1)]    // fully serialized
    [InlineData(40, 8)]
    public async Task never_exceeds_the_cap(int cellCount, int cap)
    {
        var peak = await RunAndMeasurePeak(cellCount, cap);
        peak.ShouldBeLessThanOrEqualTo(cap);
    }

    [Fact]
    public async Task actually_reaches_the_cap_when_there_is_enough_work()
    {
        // Guards against a false-pass where the cap "holds" only because nothing ever ran in parallel.
        var peak = await RunAndMeasurePeak(cellCount: 256, cap: 4);
        peak.ShouldBe(4);
    }

    [Fact]
    public async Task all_cells_run_exactly_once()
    {
        var names = Enumerable.Range(0, 100).Select(i => $"cell-{i}").ToArray();
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();

        await ProjectionHost.RebuildProjectionsWithCapAsync(names, 4, CancellationToken.None,
            (name, _) =>
            {
                seen.Add(name);
                return Task.CompletedTask;
            });

        seen.OrderBy(x => x).ShouldBe(names.OrderBy(x => x));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task non_positive_cap_runs_unbounded_without_deadlock(int cap)
    {
        // MaxDegreeOfParallelism = 0 throws; the helper must coerce non-positive to -1 (unbounded)
        // so a misconfigured cap can never wedge a rebuild.
        var names = Enumerable.Range(0, 20).Select(i => $"cell-{i}").ToArray();
        var ran = 0;

        await ProjectionHost.RebuildProjectionsWithCapAsync(names, cap, CancellationToken.None,
            (_, _) =>
            {
                Interlocked.Increment(ref ran);
                return Task.CompletedTask;
            });

        ran.ShouldBe(20);
    }
}
