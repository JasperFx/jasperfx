using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// jasperfx#1027: the jasperfx#539 heartbeat proves the poll loop is cycling, not that the mark is moving. The
// watchdog now also probes the committed ceiling directly, outside the loop, once the mark has been still for
// HighWaterProgressStallThreshold, and restarts the agent if events are committed above it. A restart re-reads
// the mark, so none of this can ever advance it past what the detector reports.
public class HighWaterAgent_progress_stall_Tests
{
    private static DaemonSettings settings(TimeSpan? stallThreshold) => new()
    {
        FastPollingTime = 10.Milliseconds(),
        SlowPollingTime = 10.Milliseconds(),
        HealthCheckPollingTime = 40.Milliseconds(),
        HighWaterStalenessThreshold = 200.Milliseconds(),
        HighWaterProgressStallThreshold = stallThreshold
    };

    // The loop cycles happily (fresh heartbeat, so IsStale never trips) but the detector keeps reporting 5 while the
    // database holds events up to 20. The probe sees it and the watchdog restarts the agent, without moving the mark.
    [Fact]
    public async Task restarts_a_cycling_agent_whose_mark_is_stuck_below_committed_events()
    {
        using var cts = new CancellationTokenSource();
        var detector = new StuckDetector(mark: 5, committedCeiling: 20);
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var recorder = new RecordingObserver();
        using var subscription = tracker.Subscribe(recorder);

        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.stall"), detector, tracker,
            NullLogger.Instance, settings(200.Milliseconds()), cts.Token);
        await agent.StartAsync();

        (await recorder.WaitForCount(s => s.Action == ShardAction.Restarted, 1, 5.Seconds())).ShouldBeTrue();

        // The same detector with the check off never restarts (a_null_threshold_disables_the_probe), so this
        // restart came from the probe, not from the staleness check.
        detector.CeilingCalls.ShouldBeGreaterThanOrEqualTo(1);
        agent.IsRunning.ShouldBeTrue();
        tracker.HighWaterMark.ShouldBe(5);
        recorder.States.ShouldAllBe(s => s.Sequence <= 5);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // A quiet store: the mark never moves, but there is nothing committed above it either. The probe runs and finds
    // nothing, so there is no restart.
    [Fact]
    public async Task does_not_restart_a_quiet_store_whose_mark_is_at_the_committed_ceiling()
    {
        using var cts = new CancellationTokenSource();
        var detector = new StuckDetector(mark: 5, committedCeiling: 5);
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var recorder = new RecordingObserver();
        using var subscription = tracker.Subscribe(recorder);

        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.quiet"), detector, tracker,
            NullLogger.Instance, settings(200.Milliseconds()), cts.Token);
        await agent.StartAsync();

        (await waitFor(() => detector.CeilingCalls >= 2, 5.Seconds())).ShouldBeTrue();

        recorder.States.ShouldNotContain(s => s.Action == ShardAction.Restarted);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // A probe that cannot reach the database is not evidence of a stall, and must not trigger a restart.
    [Fact]
    public async Task a_failing_probe_is_not_a_stall()
    {
        using var cts = new CancellationTokenSource();
        var detector = new StuckDetector(mark: 5, committedCeiling: 20) { CeilingThrows = true };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var recorder = new RecordingObserver();
        using var subscription = tracker.Subscribe(recorder);

        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.probe-fails"), detector, tracker,
            NullLogger.Instance, settings(200.Milliseconds()), cts.Token);
        await agent.StartAsync();

        (await waitFor(() => detector.CeilingCalls >= 2, 5.Seconds())).ShouldBeTrue();

        recorder.States.ShouldNotContain(s => s.Action == ShardAction.Restarted);
        agent.IsRunning.ShouldBeTrue();

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // Null turns the check off entirely: not even the probe query runs.
    [Fact]
    public async Task a_null_threshold_disables_the_probe()
    {
        using var cts = new CancellationTokenSource();
        var detector = new StuckDetector(mark: 5, committedCeiling: 20);
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var recorder = new RecordingObserver();
        using var subscription = tracker.Subscribe(recorder);

        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.stall-off"), detector, tracker,
            NullLogger.Instance, settings(null), cts.Token);
        await agent.StartAsync();

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        detector.CeilingCalls.ShouldBe(0);
        recorder.States.ShouldNotContain(s => s.Action == ShardAction.Restarted);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    [Fact]
    public void the_check_is_on_by_default()
    {
        new DaemonSettings().HighWaterProgressStallThreshold.ShouldBe(2.Minutes());
    }

    private static async Task<bool> waitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20.Milliseconds());
        }

        return condition();
    }

    // Detect always reports a caught-up mark (CurrentMark == HighestSequence), so the poll loop cycles cleanly and
    // never sees anything wrong. The committed ceiling is reported separately, as a store's max(seq_id) would be.
    private sealed class StuckDetector : IHighWaterDetector
    {
        private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly long _mark;
        private readonly long _committedCeiling;
        private int _ceilingCalls;

        public StuckDetector(long mark, long committedCeiling)
        {
            _mark = mark;
            _committedCeiling = committedCeiling;
        }

        public bool CeilingThrows { get; init; }
        public int CeilingCalls => Volatile.Read(ref _ceilingCalls);

        public Uri DatabaseUri { get; } = new("fake://stuck-db");
        public bool SupportsTenantPartitioning => false;

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics
            {
                CurrentMark = _mark, HighestSequence = _mark, LastMark = _mark, Timestamp = Timestamp
            });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<long> FetchCommittedHighWaterCeilingAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _ceilingCalls);
            return CeilingThrows
                ? Task.FromException<long>(new TimeoutException("Timeout during connection attempt"))
                : Task.FromResult(_committedCeiling);
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
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (_states.Count(predicate) >= count)
                    {
                        return true;
                    }
                }

                await Task.Delay(20.Milliseconds());
            }

            lock (_lock)
            {
                return _states.Count(predicate) >= count;
            }
        }
    }
}
