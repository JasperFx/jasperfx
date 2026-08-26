using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// jasperfx#709: HighWaterAgent.IsRunning used to be set on the FIRST line of StartAsync, before the
// Detect() it is meant to gate. The daemon's `if (!_highWater.IsRunning) await
// StartHighWaterDetectionAsync()` therefore let a concurrent starter skip the priming and read
// Tracker.HighWaterMark while it was still 0 — seeding an agent below its own committed position.
// Field-reachable under Wolverine-managed distribution, which starts agents 25-way parallel by default.
public class HighWaterPrimingRaceTests
{
    private static HighWaterAgent buildAgent(IHighWaterDetector detector, ShardStateTracker tracker,
        CancellationToken token)
    {
        var settings = new DaemonSettings { Wakeup = new NeverWakeup() };
        return new HighWaterAgent(new Meter("jasperfx.tests.highwater.priming"), detector, tracker,
            NullLogger.Instance, settings, token);
    }

    [Fact]
    public async Task is_running_stays_false_until_the_first_detection_completes()
    {
        using var cts = new CancellationTokenSource();
        var detector = new GatedDetector();
        var tracker = new ShardStateTracker(NullLogger.Instance);

        var agent = buildAgent(detector, tracker, cts.Token);

        var starting = agent.StartAsync();
        (await detector.EnteredDetectAsync()).ShouldBeTrue();

        // The whole bug in one assertion: a concurrent caller checking this flag mid-detection must not
        // be told the agent is running, because the tracker's mark is still 0 at this point.
        agent.IsRunning.ShouldBeFalse();
        tracker.HighWaterMark.ShouldBe(0);

        detector.Release(1000);
        await starting;

        agent.IsRunning.ShouldBeTrue();
        tracker.HighWaterMark.ShouldBe(1000);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    [Fact]
    public async Task a_failed_detection_leaves_the_agent_not_running()
    {
        using var cts = new CancellationTokenSource();
        var detector = new GatedDetector();
        var tracker = new ShardStateTracker(NullLogger.Instance);

        var agent = buildAgent(detector, tracker, cts.Token);

        var starting = agent.StartAsync();
        (await detector.EnteredDetectAsync()).ShouldBeTrue();
        detector.Fail(new InvalidOperationException("the database is down"));

        await Should.ThrowAsync<InvalidOperationException>(async () => await starting);

        // Previously IsRunning stayed true after a failed Detect, so every later start skipped the
        // priming forever and seeded from a mark that had never been read.
        agent.IsRunning.ShouldBeFalse();
    }

    private sealed class GatedDetector : IHighWaterDetector
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _detectCount;

        public Uri DatabaseUri { get; } = new("fake://db1");

        // Skips HighWaterAgent's recurring poll loop, so DetectCount only ever reflects the priming.
        public bool SupportsTenantPartitioning => true;

        public int DetectCount => Volatile.Read(ref _detectCount);

        public Task<bool> EnteredDetectAsync() => _entered.Task.WaitAsync(10.Seconds()).ContinueWith(_ => true);

        public void Release(long mark) => _release.TrySetResult(mark);

        public void Fail(Exception e) => _release.TrySetException(e);

        public async Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            Interlocked.Increment(ref _detectCount);
            _entered.TrySetResult();

            var mark = await _release.Task.ConfigureAwait(false);

            return new HighWaterStatistics { CurrentMark = mark, LastMark = mark, HighestSequence = mark };
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
            => Task.FromResult(new HighWaterVector([]));

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
            => DetectForTenantsAsync(tenantIds, token);
    }

    private sealed class NeverWakeup : IDaemonWakeup
    {
        public Task WaitAsync(TimeSpan timeout, CancellationToken token) => Task.Delay(Timeout.Infinite, token);

        public void Dispose()
        {
        }
    }
}
