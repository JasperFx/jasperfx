using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// marten#4953: CheckNowAsync (driven by rebuilds and catch-up) used HighWaterStatistics.HighestSequence
// as its loop target. That value can be a database sequence's last_value, which includes numbers merely
// RESERVED by in-flight or rolled-back transactions — so the loop either pressured the safe-zone
// detection into skipping over in-flight appends (silently losing their events) or spun forever on a
// rolled-back tail. The loop now targets the detector's COMMITTED ceiling and is time-bounded.
public class HighWaterAgent_check_now_committed_ceiling_Tests
{
    private static HighWaterAgent buildAgent(IHighWaterDetector detector, ShardStateTracker tracker,
        CancellationToken token, string meter)
    {
        var settings = new DaemonSettings { SlowPollingTime = 25.Milliseconds() };
        return new HighWaterAgent(new Meter(meter), detector, tracker, NullLogger.Instance, settings, token);
    }

    // Reserved sequence numbers far above the committed height (an import in flight, or a rolled-back
    // tail) must not keep CheckNowAsync looping — the committed ceiling is the target.
    [Fact]
    public async Task check_now_targets_the_committed_ceiling_not_the_reserved_highest_sequence()
    {
        using var cts = new CancellationTokenSource();
        var detector = new StubDetector(committedCeiling: 12, marks: [12]) { ReservedHighest = 100 };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var agent = buildAgent(detector, tracker, cts.Token, "jasperfx.tests.checknow.committed");

        var completed = agent.CheckNowAsync();
        var winner = await Task.WhenAny(completed, Task.Delay(5.Seconds()));

        winner.ShouldBe(completed);
        tracker.HighWaterMark.ShouldBe(12);
        await cts.CancelAsync();
    }

    // A detector that holds before an in-flight gap (marten#4953 transaction evidence) makes the loop
    // WAIT; once the append lands and the mark reaches the committed ceiling, CheckNowAsync completes.
    [Fact]
    public async Task check_now_waits_for_a_holding_detector_until_the_ceiling_is_reached()
    {
        using var cts = new CancellationTokenSource();
        var detector = new StubDetector(committedCeiling: 12, marks: [8, 8, 8, 12]) { ReservedHighest = 12 };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var agent = buildAgent(detector, tracker, cts.Token, "jasperfx.tests.checknow.waits");

        var completed = agent.CheckNowAsync();
        var winner = await Task.WhenAny(completed, Task.Delay(5.Seconds()));

        winner.ShouldBe(completed);
        tracker.HighWaterMark.ShouldBe(12);
        detector.SafeZoneCalls.ShouldBeGreaterThanOrEqualTo(4);
        await cts.CancelAsync();
    }

    // The timeout backstop: a detector pinned below the ceiling (e.g. a leaked idle-in-transaction
    // session upstream) cannot hang CheckNowAsync forever.
    [Fact]
    public async Task check_now_gives_up_at_the_timeout_instead_of_hanging()
    {
        using var cts = new CancellationTokenSource();
        var detector = new StubDetector(committedCeiling: 12, marks: [8]) { ReservedHighest = 12 };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var agent = buildAgent(detector, tracker, cts.Token, "jasperfx.tests.checknow.timeout");
        agent.CheckNowTimeout = 300.Milliseconds();

        var completed = agent.CheckNowAsync();
        var winner = await Task.WhenAny(completed, Task.Delay(5.Seconds()));

        winner.ShouldBe(completed);
        tracker.HighWaterMark.ShouldBe(8);
        await cts.CancelAsync();
    }

    // Returns marks[i] for the i-th DetectInSafeZone call, clamping at the last entry.
    private sealed class StubDetector: IHighWaterDetector
    {
        private readonly long _committedCeiling;
        private readonly long[] _marks;
        private int _safeZoneCalls;

        public StubDetector(long committedCeiling, long[] marks)
        {
            _committedCeiling = committedCeiling;
            _marks = marks;
        }

        public long ReservedHighest { get; set; }

        public int SafeZoneCalls => Volatile.Read(ref _safeZoneCalls);

        public Uri DatabaseUri { get; } = new("fake://checknow-db");

        public Task<long> FetchCommittedHighWaterCeilingAsync(CancellationToken token)
        {
            return Task.FromResult(_committedCeiling);
        }

        public Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            return Task.FromResult(new HighWaterStatistics
            {
                CurrentMark = _marks[^1], HighestSequence = ReservedHighest
            });
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token)
        {
            var call = Interlocked.Increment(ref _safeZoneCalls);
            var mark = _marks[Math.Min(call - 1, _marks.Length - 1)];
            return Task.FromResult(new HighWaterStatistics
            {
                CurrentMark = mark, HighestSequence = ReservedHighest
            });
        }
    }
}
