using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// jasperfx#539: the store-global high-water poll loop (Path A) now publishes a per-cycle liveness heartbeat
// — proof it is *cycling*, independent of whether the mark is *advancing* — surfaces staleness, and the
// watchdog restarts a loop that has stopped completing cycles, not only one that faulted (#524). None of
// the new behavior ever advances the high-water mark.
public class HighWaterAgent_liveness_Tests
{
    private static HighWaterAgent buildAgent(IHighWaterDetector detector, ShardStateTracker tracker,
        DaemonSettings settings, CancellationToken token, string meterName)
        => new(new Meter(meterName), detector, tracker, NullLogger.Instance, settings, token);

    // Every completed cycle publishes a Running HighWaterMark state carrying a heartbeat, and none of them
    // pushes the mark past what the detector actually reports.
    [Fact]
    public async Task publishes_a_running_heartbeat_every_cycle_without_advancing_the_mark()
    {
        using var cts = new CancellationTokenSource();
        var detector = new SteadyDetector(currentMark: 5);
        var settings = new DaemonSettings
        {
            FastPollingTime = 20.Milliseconds(),
            SlowPollingTime = 20.Milliseconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var recorder = new RecordingObserver();
        using var subscription = tracker.Subscribe(recorder);

        var agent = buildAgent(detector, tracker, settings, cts.Token, "jasperfx.tests.highwater.heartbeat");
        await agent.StartAsync();

        // Several cycles' worth of heartbeats arrive, each Running.
        (await recorder.WaitForCount(s => s.AgentStatus == "Running", 3, 5.Seconds())).ShouldBeTrue();

        agent.LastPolledAt.ShouldNotBeNull();
        agent.IsStale(5.Seconds(), DateTimeOffset.UtcNow).ShouldBeFalse();

        // The detector never advances, so the heartbeats must never move the mark off 5.
        tracker.HighWaterMark.ShouldBe(5);
        recorder.States.ShouldAllBe(s => s.Sequence <= 5);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // A wakeup that stops firing (returns for its first calls, then blocks forever) wedges the loop: it stops
    // completing cycles, so LastPolledAt ages out, IsStale trips, and the watchdog restarts it — publishing a
    // Restarted state. The restart never advances the mark off the detector's value.
    [Fact]
    public async Task watchdog_restarts_a_wedged_loop_that_stopped_cycling()
    {
        using var cts = new CancellationTokenSource();
        var detector = new SteadyDetector(currentMark: 7);
        var wakeup = new BlockAfterWakeup(freeCalls: 2);
        var settings = new DaemonSettings
        {
            Wakeup = wakeup,
            FastPollingTime = 10.Milliseconds(),
            SlowPollingTime = 10.Milliseconds(),
            HealthCheckPollingTime = 40.Milliseconds(),
            HighWaterStalenessThreshold = 200.Milliseconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var recorder = new RecordingObserver();
        using var subscription = tracker.Subscribe(recorder);

        var agent = buildAgent(detector, tracker, settings, cts.Token, "jasperfx.tests.highwater.stale");
        await agent.StartAsync();

        // The loop parks in the wakeup after its free calls, goes stale, and the watchdog restarts it.
        (await recorder.WaitForCount(s => s.Action == ShardAction.Restarted, 1, 5.Seconds())).ShouldBeTrue();
        tracker.HighWaterMark.ShouldBe(7);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // The explicit restart seam re-establishes the loop, republishes a Restarted state, and never advances
    // the mark — a health check can remediate in-process without risking a forced skip.
    [Fact]
    public async Task restart_async_re_establishes_the_loop_without_advancing_the_mark()
    {
        using var cts = new CancellationTokenSource();
        var detector = new SteadyDetector(currentMark: 3);
        var settings = new DaemonSettings
        {
            FastPollingTime = 20.Milliseconds(),
            SlowPollingTime = 20.Milliseconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var recorder = new RecordingObserver();
        using var subscription = tracker.Subscribe(recorder);

        var agent = buildAgent(detector, tracker, settings, cts.Token, "jasperfx.tests.highwater.restart");
        await agent.StartAsync();
        (await recorder.WaitForCount(s => s.AgentStatus == "Running", 1, 5.Seconds())).ShouldBeTrue();

        await agent.RestartAsync();

        // The Restarted state is published through the tracker's async block, so wait for it to be observed
        // rather than reading immediately after the await (which only guarantees it was posted).
        (await recorder.WaitForCount(s => s.Action == ShardAction.Restarted, 1, 5.Seconds())).ShouldBeTrue();
        agent.IsRunning.ShouldBeTrue();
        tracker.HighWaterMark.ShouldBe(3);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // Reports a fixed, caught-up mark forever (CurrentMark == HighestSequence), so the loop cycles cleanly
    // without ever advancing the mark — isolating the heartbeat/liveness behavior from mark movement.
    private sealed class SteadyDetector : IHighWaterDetector
    {
        private readonly long _currentMark;
        private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public SteadyDetector(long currentMark) => _currentMark = currentMark;

        public Uri DatabaseUri { get; } = new("fake://liveness-db");
        public bool SupportsTenantPartitioning => false;

        private Task<HighWaterStatistics> stat()
            => Task.FromResult(new HighWaterStatistics
            {
                CurrentMark = _currentMark, HighestSequence = _currentMark, LastMark = _currentMark,
                Timestamp = Timestamp
            });

        public Task<HighWaterStatistics> Detect(CancellationToken token) => stat();
        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => stat();
    }

    // Returns immediately for its first N calls, then blocks forever (honoring cancellation) — modelling a
    // custom IDaemonWakeup that stops firing while the loop is otherwise alive.
    private sealed class BlockAfterWakeup : IDaemonWakeup
    {
        private readonly int _freeCalls;
        private int _calls;

        public BlockAfterWakeup(int freeCalls) => _freeCalls = freeCalls;

        public Task WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            return Interlocked.Increment(ref _calls) <= _freeCalls
                ? Task.CompletedTask
                : Task.Delay(Timeout.Infinite, token);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingObserver : IObserver<ShardState>
    {
        private readonly object _lock = new();
        private readonly List<ShardState> _states = [];

        public IReadOnlyList<ShardState> States
        {
            get
            {
                lock (_lock)
                {
                    return _states.ToList();
                }
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value)
        {
            lock (_lock)
            {
                _states.Add(value);
            }
        }

        public async Task<bool> WaitForCount(Func<ShardState, bool> predicate, int count, TimeSpan timeout)
        {
            var elapsed = TimeSpan.Zero;
            var step = 20.Milliseconds();
            while (elapsed < timeout)
            {
                lock (_lock)
                {
                    if (_states.Count(predicate) >= count)
                    {
                        return true;
                    }
                }

                await Task.Delay(step);
                elapsed += step;
            }

            lock (_lock)
            {
                return _states.Count(predicate) >= count;
            }
        }
    }
}
