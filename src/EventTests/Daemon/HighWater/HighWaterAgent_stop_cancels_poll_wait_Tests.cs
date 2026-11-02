using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// wolverine#5318: StopAsync awaits the poll loop, and the loop used to wait out its current
// FastPollingTime / SlowPollingTime tick before it noticed the stop. Every pause of the daemon
// (Wolverine's PauseThenCatchUpOnMartenDaemonActivity among them) paid up to a full polling interval.
// Stopping now cancels the loop's own token, so the wait ends at once.
public class HighWaterAgent_stop_cancels_poll_wait_Tests
{
    [Fact]
    public async Task stop_returns_well_under_the_polling_time_and_the_agent_can_start_again()
    {
        using var cts = new CancellationTokenSource();
        var detector = new CountingDetector();
        var settings = new DaemonSettings
        {
            FastPollingTime = 5.Seconds(),
            SlowPollingTime = 5.Seconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);

        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.stop_cancels_wait"), detector, tracker,
            NullLogger.Instance, settings, cts.Token);

        await agent.StartAsync();
        (await detector.WaitForDetects(2, 5.Seconds())).ShouldBeTrue();

        // The loop is now parked in its 5 second poll wait
        await Task.Delay(100.Milliseconds());

        var stopwatch = Stopwatch.StartNew();
        await agent.StopAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(1.Seconds());
        agent.IsRunning.ShouldBeFalse();

        // A stop must not leave the agent unable to start again: the new loop gets its own token
        var afterStop = detector.DetectCount;
        await agent.StartAsync();
        agent.IsRunning.ShouldBeTrue();
        (await detector.WaitForDetects(afterStop + 2, 5.Seconds())).ShouldBeTrue();

        await Task.Delay(100.Milliseconds());

        stopwatch.Restart();
        await agent.StopAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(1.Seconds());

        await cts.CancelAsync();
        agent.Dispose();
    }

    private sealed class CountingDetector: IHighWaterDetector
    {
        private int _detectCount;

        public Uri DatabaseUri { get; } = new("fake://stop-cancels-wait-db");
        public bool SupportsTenantPartitioning => false;
        public int DetectCount => Volatile.Read(ref _detectCount);

        public Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            Interlocked.Increment(ref _detectCount);
            return Task.FromResult(new HighWaterStatistics { CurrentMark = 0, HighestSequence = 0 });
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public async Task<bool> WaitForDetects(int expected, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (DetectCount >= expected) return true;
                await Task.Delay(10.Milliseconds());
            }

            return DetectCount >= expected;
        }
    }
}
