using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// CritterWatch#675: the standalone, display-only high-water monitor — just the high-water agent for a single
// database, with no projection shards attached — so a monitoring tool can show a live event-store ceiling for a
// store whose projections are all Inline/Live and therefore run no async daemon.
public class HighWaterMonitorTests
{
    private static HighWaterMonitor buildMonitor(SeedingDetector detector)
    {
        var settings = new DaemonSettings { Wakeup = new BlockingWakeup() };
        return new HighWaterMonitor(new Meter("jasperfx.tests.highwatermonitor"), detector, settings,
            NullLogger.Instance);
    }

    [Fact]
    public async Task starting_seeds_the_current_mark_onto_the_tracker()
    {
        using var cts = new CancellationTokenSource();
        var detector = new SeedingDetector(seedMark: 100);
        var monitor = buildMonitor(detector);

        monitor.IsRunning.ShouldBeFalse();
        monitor.CurrentMark.ShouldBe(0);

        await monitor.StartAsync(cts.Token);

        monitor.IsRunning.ShouldBeTrue();
        monitor.CurrentMark.ShouldBe(100);
        monitor.Tracker.HighWaterMark.ShouldBe(100);
        monitor.DatabaseUri.ShouldBe(detector.DatabaseUri);

        await monitor.StopAsync();
    }

    [Fact]
    public async Task can_be_stopped_and_restarted()
    {
        using var cts = new CancellationTokenSource();
        var detector = new SeedingDetector(seedMark: 42);
        var monitor = buildMonitor(detector);

        await monitor.StartAsync(cts.Token);
        monitor.IsRunning.ShouldBeTrue();

        await monitor.StopAsync();
        monitor.IsRunning.ShouldBeFalse();

        await monitor.StartAsync(cts.Token);
        monitor.IsRunning.ShouldBeTrue();

        await monitor.StopAsync();
    }

    [Fact]
    public async Task detect_is_an_on_demand_read_that_does_not_require_starting()
    {
        using var cts = new CancellationTokenSource();
        var detector = new SeedingDetector(seedMark: 7);
        var monitor = buildMonitor(detector);

        var stats = await monitor.DetectAsync(cts.Token);

        stats.CurrentMark.ShouldBe(7);
        monitor.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task dispose_stops_the_monitor()
    {
        using var cts = new CancellationTokenSource();
        var detector = new SeedingDetector(seedMark: 5);
        var monitor = buildMonitor(detector);

        await monitor.StartAsync(cts.Token);
        monitor.IsRunning.ShouldBeTrue();

        await monitor.DisposeAsync();
        monitor.IsRunning.ShouldBeFalse();
    }

    // A partitioning detector seeds the mark once at StartAsync and never runs the recurring loop, which keeps the
    // seed deterministic while still exercising the publish-to-tracker path.
    private sealed class SeedingDetector: IHighWaterDetector
    {
        private readonly long _seedMark;

        public SeedingDetector(long seedMark) => _seedMark = seedMark;

        public Uri DatabaseUri { get; } = new("fake://display-only-db");

        // Skips the recurring loop so the seed value is the only mark published — makes the assertions deterministic.
        public bool SupportsTenantPartitioning => true;

        public Task<HighWaterStatistics> Detect(CancellationToken token) =>
            Task.FromResult(new HighWaterStatistics { CurrentMark = _seedMark, HighestSequence = _seedMark });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    private sealed class BlockingWakeup: IDaemonWakeup
    {
        public Task WaitAsync(TimeSpan timeout, CancellationToken token) => Task.Delay(Timeout.Infinite, token);

        public void Dispose()
        {
        }
    }
}
