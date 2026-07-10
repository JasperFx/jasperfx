using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// #4913: under per-tenant event partitioning the store-global HighWaterAgent must NOT run its recurring
// Detect() loop (Marten emits that as `max(seq_id)` fanned out across every tenant partition). Tenant high
// water is driven per-tenant by the daemon's TenantedHighWaterCoordinator, so the store-global mark is only
// seeded once at startup. When partitioning is off, the recurring loop must still run exactly as before.
public class HighWaterAgent_partitioning_skips_global_poll
{
    private static HighWaterAgent buildAgent(CountingDetector detector, BlockingWakeup wakeup, CancellationToken token)
    {
        var settings = new DaemonSettings { Wakeup = wakeup };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        return new HighWaterAgent(new Meter("jasperfx.tests.highwater"), detector, tracker,
            NullLogger.Instance, settings, token);
    }

    [Fact]
    public async Task partitioned_store_seeds_once_and_does_not_start_the_recurring_loop()
    {
        using var cts = new CancellationTokenSource();
        var detector = new CountingDetector(supportsTenantPartitioning: true);
        var wakeup = new BlockingWakeup();

        var agent = buildAgent(detector, wakeup, cts.Token);
        await agent.StartAsync();

        // The mark is seeded exactly once; the recurring loop (which would call the wakeup) never starts.
        detector.DetectCount.ShouldBe(1);
        (await wakeup.EnteredWithin(500.Milliseconds())).ShouldBeFalse();
        detector.DetectCount.ShouldBe(1);

        // Still "running" so the daemon's per-tenant high-water timer stays enabled.
        agent.IsRunning.ShouldBeTrue();

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    [Fact]
    public async Task non_partitioned_store_starts_the_recurring_loop()
    {
        using var cts = new CancellationTokenSource();
        var detector = new CountingDetector(supportsTenantPartitioning: false);
        var wakeup = new BlockingWakeup();

        var agent = buildAgent(detector, wakeup, cts.Token);
        await agent.StartAsync();

        // The loop ran an in-loop Detect() beyond the initial seed and then parked in the wakeup.
        (await wakeup.EnteredWithin(2.Seconds())).ShouldBeTrue();
        detector.DetectCount.ShouldBeGreaterThanOrEqualTo(2);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    private sealed class CountingDetector: IHighWaterDetector
    {
        private int _detectCount;

        public CountingDetector(bool supportsTenantPartitioning) =>
            SupportsTenantPartitioning = supportsTenantPartitioning;

        public Uri DatabaseUri { get; } = new("fake://db");
        public bool SupportsTenantPartitioning { get; }
        public int DetectCount => Volatile.Read(ref _detectCount);

        public Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            Interlocked.Increment(ref _detectCount);
            return Task.FromResult(new HighWaterStatistics { CurrentMark = 0, HighestSequence = 0 });
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    // Signals the first time the agent's loop reaches its inter-poll wait, then blocks forever so the loop
    // parks after a single in-loop Detect() — making DetectCount deterministic rather than time-dependent.
    private sealed class BlockingWakeup: IDaemonWakeup
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            _entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        }

        public async Task<bool> EnteredWithin(TimeSpan timeout)
        {
            var winner = await Task.WhenAny(_entered.Task, Task.Delay(timeout));
            return winner == _entered.Task;
        }

        public void Dispose()
        {
        }
    }
}
