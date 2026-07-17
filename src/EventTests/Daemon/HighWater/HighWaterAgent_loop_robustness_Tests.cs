using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// #524 (split from marten#4961): two robustness defects in the HighWaterAgent poll loop.
//  * Defect A — an IDaemonWakeup that throws (e.g. Marten's LISTEN/NOTIFY wakeup reconnecting against a downed
//    database) must not escape the loop and permanently stop high-water progress. The wakeup is now treated as
//    untrusted: a throw is logged and the loop falls back to a delay and continues.
//  * Defect C — Task.Factory.StartNew over the async loop delegate returned a Task<Task>, so the checkState
//    watchdog inspected the always-successful outer task and never saw (or restarted) a faulted loop. The loop
//    is now unwrapped, so a fault is observed and the loop restarts itself.
public class HighWaterAgent_loop_robustness_Tests
{
    // Defect A: a wakeup that throws does not tear down the poll loop — the loop swallows it, waits, and keeps
    // polling, so it reaches a *second* wakeup after at least one more Detect().
    [Fact]
    public async Task a_throwing_wakeup_does_not_kill_the_poll_loop()
    {
        using var cts = new CancellationTokenSource();
        var detector = new CountingDetector();
        var wakeup = new ThrowOnceThenBlockWakeup();
        var settings = new DaemonSettings { Wakeup = wakeup };
        var tracker = new ShardStateTracker(NullLogger.Instance);

        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.defectA"), detector, tracker,
            NullLogger.Instance, settings, cts.Token);

        await agent.StartAsync();

        // With the bug the first wakeup throw escapes detectChanges and the loop dies here; with the fix the
        // loop recovers and reaches the (blocking) second wakeup.
        (await wakeup.ReachedSecondWaitWithin(5.Seconds())).ShouldBeTrue();
        detector.DetectCount.ShouldBeGreaterThanOrEqualTo(2);
        agent.IsRunning.ShouldBeTrue();

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // Defect C: a loop that faults (here through the unguarded DetectInSafeZone throw in the stale branch) is
    // seen by the watchdog and restarted, so the recovered detector advances the mark on its own.
    [Fact]
    public async Task the_watchdog_restarts_a_faulted_loop()
    {
        using var cts = new CancellationTokenSource();
        var detector = new FaultThenRecoverDetector();
        var settings = new DaemonSettings
        {
            // StaleThreshold=0 forces the stale branch straight into DetectInSafeZone; small poll/health
            // intervals keep the fault-then-restart cycle quick and deterministic.
            StaleSequenceThreshold = TimeSpan.Zero,
            FastPollingTime = 25.Milliseconds(),
            SlowPollingTime = 25.Milliseconds(),
            HealthCheckPollingTime = 200.Milliseconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);

        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.defectC"), detector, tracker,
            NullLogger.Instance, settings, cts.Token);

        await agent.StartAsync();

        // First pass faults the loop. With the unwrap fix the watchdog restarts it and the mark reaches 10;
        // with the bug the outer Task<Task> never reports IsFaulted, so the loop is never restarted and the
        // mark is stuck at 0.
        (await waitForMark(tracker, 10, 5.Seconds())).ShouldBeTrue();

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    private static async Task<bool> waitForMark(ShardStateTracker tracker, long expected, TimeSpan timeout)
    {
        var elapsed = TimeSpan.Zero;
        var step = 25.Milliseconds();
        while (elapsed < timeout)
        {
            if (tracker.HighWaterMark >= expected)
            {
                return true;
            }

            await Task.Delay(step);
            elapsed += step;
        }

        return tracker.HighWaterMark >= expected;
    }

    private sealed class CountingDetector: IHighWaterDetector
    {
        private int _detectCount;

        public Uri DatabaseUri { get; } = new("fake://defect-a-db");
        public bool SupportsTenantPartitioning => false;
        public int DetectCount => Volatile.Read(ref _detectCount);

        public Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            Interlocked.Increment(ref _detectCount);
            return Task.FromResult(new HighWaterStatistics { CurrentMark = 0, HighestSequence = 0 });
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    // Throws on the first wakeup (as a reconnecting LISTEN/NOTIFY wakeup would against a downed DB), then signals
    // and blocks forever so the loop parks deterministically at its second wakeup.
    private sealed class ThrowOnceThenBlockWakeup: IDaemonWakeup
    {
        private int _calls;
        private readonly TaskCompletionSource _secondWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("Simulated wakeup connection failure");
            }

            _secondWait.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        }

        public async Task<bool> ReachedSecondWaitWithin(TimeSpan timeout)
        {
            var winner = await Task.WhenAny(_secondWait.Task, Task.Delay(timeout));
            return winner == _secondWait.Task;
        }

        public void Dispose()
        {
        }
    }

    // Reports a stale sequence (CurrentMark behind HighestSequence) so the agent drops into the stale branch,
    // where the first DetectInSafeZone throws and faults the loop. After the watchdog restarts the loop, the
    // second DetectInSafeZone recovers and reports a caught-up mark of 10.
    private sealed class FaultThenRecoverDetector: IHighWaterDetector
    {
        // A fixed, non-default timestamp shared by the seed and the stale reading so that, with
        // StaleSequenceThreshold=0, safeHarborTime == the reading's timestamp and the branch proceeds to
        // DetectInSafeZone instead of parking.
        private static readonly DateTimeOffset Timestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly object _lock = new();
        private bool _safeZoneThrew;
        private bool _recovered;

        public Uri DatabaseUri { get; } = new("fake://faulting-db");
        public bool SupportsTenantPartitioning => false;

        public Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            lock (_lock)
            {
                return Task.FromResult(_recovered
                    ? new HighWaterStatistics { CurrentMark = 10, HighestSequence = 10, Timestamp = Timestamp }
                    : new HighWaterStatistics { CurrentMark = 0, HighestSequence = 10, Timestamp = Timestamp });
            }
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token)
        {
            lock (_lock)
            {
                if (!_safeZoneThrew)
                {
                    _safeZoneThrew = true;
                    throw new InvalidOperationException("Simulated connection failure inside the high-water loop");
                }

                _recovered = true;
                return Task.FromResult(new HighWaterStatistics
                {
                    CurrentMark = 10, HighestSequence = 10, Timestamp = Timestamp
                });
            }
        }
    }
}
