using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// jasperfx#1027: the watchdog's RestartAsync used to tear the agent down (timer stopped, IsRunning cleared)
// BEFORE re-detecting the mark, so a Detect that threw because the database was still down left the agent with
// no loop, no timer and no way back. High water stayed frozen until the process was redeployed.
public class HighWaterAgent_restart_failure_Tests
{
    // The reporter's repro. The database goes down while the poll loop is mid-Detect, the watchdog's RestartAsync
    // runs while it is still down, then the database comes back with new events. The agent should resume tracking
    // the mark.
    [Fact]
    public async Task agent_recovers_after_a_watchdog_restart_fails_while_the_database_is_down()
    {
        using var cts = new CancellationTokenSource();
        var detector = new OutageDetector(initialMark: 5);
        var settings = new DaemonSettings
        {
            FastPollingTime = 10.Milliseconds(),
            SlowPollingTime = 10.Milliseconds(),
            HealthCheckPollingTime = 40.Milliseconds(),
            HighWaterStalenessThreshold = 200.Milliseconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.restart-failure"), detector, tracker,
            NullLogger.Instance, settings, cts.Token);

        await agent.StartAsync();
        (await waitFor(() => tracker.HighWaterMark == 5, 5.Seconds())).ShouldBeTrue();

        // The loop's in-flight Detect hangs on the dead connection until the database comes back, so the loop
        // stops cycling and goes stale. Any new connection attempt fails fast.
        detector.GoDown();
        (await waitFor(() => detector.HungCalls >= 1, 5.Seconds())).ShouldBeTrue();

        // The watchdog notices the stale loop and calls RestartAsync, whose StartAsync -> Detect throws.
        (await waitFor(() => detector.FailedCalls >= 1, 5.Seconds())).ShouldBeTrue();

        // The database comes back with new events; the hung Detect returns.
        detector.ComeBackUp(newMark: 10);

        // Several staleness windows: the watchdog has had ample opportunity to retry.
        await waitFor(() => tracker.HighWaterMark == 10, 3.Seconds());

        agent.IsRunning.ShouldBeTrue();
        tracker.HighWaterMark.ShouldBe(10);

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // The reporter's test can pass because the OLD loop's hung Detect returns once the database is back. Here it
    // never returns (a connection that is simply gone), so the only way back is the watchdog retrying the restart
    // until the database answers, which is what used to be impossible: the first failed restart stopped the timer.
    [Fact]
    public async Task the_watchdog_keeps_retrying_a_failed_restart_until_the_database_answers()
    {
        using var cts = new CancellationTokenSource();
        var detector = new OutageDetector(initialMark: 5) { HungCallNeverReturns = true };
        var settings = new DaemonSettings
        {
            FastPollingTime = 10.Milliseconds(),
            SlowPollingTime = 10.Milliseconds(),
            HealthCheckPollingTime = 40.Milliseconds(),
            HighWaterStalenessThreshold = 200.Milliseconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.restart-retry"), detector, tracker,
            NullLogger.Instance, settings, cts.Token);

        await agent.StartAsync();
        (await waitFor(() => tracker.HighWaterMark == 5, 5.Seconds())).ShouldBeTrue();

        detector.GoDown();

        // More than one failed restart: the watchdog came back for a second attempt after the first one threw,
        // and the agent still reports itself running throughout.
        (await waitFor(() => detector.FailedCalls >= 2, 5.Seconds())).ShouldBeTrue();
        agent.IsRunning.ShouldBeTrue();

        detector.ComeBackUp(newMark: 10);

        (await waitFor(() => tracker.HighWaterMark == 10, 5.Seconds())).ShouldBeTrue();
        agent.IsRunning.ShouldBeTrue();

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // A loop the watchdog has replaced can still be parked inside a Detect that answers much later, with a reading
    // taken before the replacement started. It must not publish that reading: the tracker would go backwards.
    [Fact]
    public async Task a_retired_loop_that_wakes_up_late_never_writes_its_old_mark()
    {
        using var cts = new CancellationTokenSource();
        var detector = new LateAnswerDetector(initialMark: 5);
        var settings = new DaemonSettings
        {
            FastPollingTime = 10.Milliseconds(),
            SlowPollingTime = 10.Milliseconds(),
            HealthCheckPollingTime = 40.Milliseconds(),
            HighWaterStalenessThreshold = 200.Milliseconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var marks = new List<long>();
        using var subscription = tracker.Subscribe(new MarkRecorder(marks));
        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.retired-loop"), detector, tracker,
            NullLogger.Instance, settings, cts.Token);

        await agent.StartAsync();
        (await waitFor(() => tracker.HighWaterMark == 5, 5.Seconds())).ShouldBeTrue();

        // The loop's next Detect hangs holding a reading of 5; meanwhile the store moves on to 10, the loop goes
        // stale and the watchdog's restart brings up a fresh loop that sees 10.
        detector.HangNextCall();
        (await waitFor(() => detector.Hung, 5.Seconds())).ShouldBeTrue();
        detector.Advance(10);
        (await waitFor(() => tracker.HighWaterMark == 10, 5.Seconds())).ShouldBeTrue();

        // Now the retired loop's Detect finally answers, with 5.
        detector.ReleaseHungCall();
        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);

        tracker.HighWaterMark.ShouldBe(10);
        lock (marks)
        {
            marks.SkipWhile(x => x < 10).ShouldAllBe(x => x >= 10);
        }

        await cts.CancelAsync();
        await agent.StopAsync();
    }

    // RestartAsync no longer clears IsRunning while it detects, so a StopAsync that lands during a watchdog
    // restart's (now bounded, but possibly long) Detect must win: the restart must not bring the loop back.
    [Fact]
    public async Task a_stop_during_a_watchdog_restart_is_not_undone_by_the_restart()
    {
        using var cts = new CancellationTokenSource();
        var detector = new GatedDetector(mark: 5);
        var settings = new DaemonSettings
        {
            FastPollingTime = 10.Milliseconds(),
            SlowPollingTime = 10.Milliseconds(),
            HealthCheckPollingTime = 40.Milliseconds(),
            // Long enough that the held restart Detect is released well inside its own timeout, so it is the
            // stop check, not a timeout, that keeps the loop from coming back.
            HighWaterStalenessThreshold = 1.Seconds()
        };
        var tracker = new ShardStateTracker(NullLogger.Instance);
        var actions = new List<ShardAction>();
        using var subscription = tracker.Subscribe(new ActionRecorder(actions));
        var agent = new HighWaterAgent(new Meter("jasperfx.tests.highwater.stop-during-restart"), detector, tracker,
            NullLogger.Instance, settings, cts.Token);

        await agent.StartAsync();
        (await waitFor(() => tracker.HighWaterMark == 5, 5.Seconds())).ShouldBeTrue();

        // Wedge the loop, then hold the watchdog's restart inside its Detect.
        var loopCall = detector.HoldNext();
        (await waitFor(() => loopCall.Entered, 5.Seconds())).ShouldBeTrue();
        var restartCall = detector.HoldNext();
        (await waitFor(() => restartCall.Entered, 5.Seconds())).ShouldBeTrue();

        // StopAsync clears IsRunning straight away, then waits for the wedged loop.
        var stopping = agent.StopAsync();
        agent.IsRunning.ShouldBeFalse();

        restartCall.Release();
        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);

        agent.IsRunning.ShouldBeFalse();
        lock (actions)
        {
            actions.ShouldNotContain(ShardAction.Restarted);
        }

        loopCall.Release();
        (await Task.WhenAny(stopping, Task.Delay(5.Seconds(), TestContext.Current.CancellationToken)))
            .ShouldBe(stopping);

        var calls = detector.Calls;
        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);
        detector.Calls.ShouldBe(calls);
        agent.IsRunning.ShouldBeFalse();

        await cts.CancelAsync();
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

    // Once "down", the first Detect hangs until the database comes back (a dead pooled connection) and every
    // later Detect throws at once (new connection attempts are refused).
    private sealed class OutageDetector : IHighWaterDetector
    {
        private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly TaskCompletionSource _databaseBack =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _mark;
        private volatile bool _down;
        private volatile bool _hungCallNeverReturns;
        private volatile bool _connectionDropped;
        private int _hungCalls;
        private int _failedCalls;

        public OutageDetector(long initialMark) => _mark = initialMark;

        public int HungCalls => Volatile.Read(ref _hungCalls);

        // True: the hung call stays hung even after ComeBackUp, as a connection that is simply gone would.
        public bool HungCallNeverReturns
        {
            get => _hungCallNeverReturns;
            init => _hungCallNeverReturns = value;
        }
        public int FailedCalls => Volatile.Read(ref _failedCalls);

        public Uri DatabaseUri { get; } = new("fake://outage-db");
        public bool SupportsTenantPartitioning => false;

        public void GoDown()
        {
            _down = true;
            _connectionDropped = false;
        }

        public void ComeBackUp(long newMark)
        {
            Interlocked.Exchange(ref _mark, newMark);
            _down = false;
            _databaseBack.TrySetResult();
        }

        public async Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            if (_down && !_connectionDropped)
            {
                _connectionDropped = true;
                Interlocked.Increment(ref _hungCalls);
                if (_hungCallNeverReturns)
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

                await _databaseBack.Task.WaitAsync(token);
            }
            else if (_down)
            {
                Interlocked.Increment(ref _failedCalls);
                throw new TimeoutException("Timeout during connection attempt");
            }

            var mark = Interlocked.Read(ref _mark);
            return new HighWaterStatistics
            {
                CurrentMark = mark, HighestSequence = mark, LastMark = mark, Timestamp = Timestamp
            };
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }
    // Answers every call with the current mark, except one call the test parks on purpose: that one captures the
    // mark at the moment it was made and only answers when released, by which time the mark has moved on.
    private sealed class LateAnswerDetector : IHighWaterDetector
    {
        private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _mark;
        private int _hangNext;
        private volatile bool _hung;

        public LateAnswerDetector(long initialMark) => _mark = initialMark;

        public bool Hung => _hung;
        public Uri DatabaseUri { get; } = new("fake://late-answer-db");
        public bool SupportsTenantPartitioning => false;

        public void HangNextCall() => Volatile.Write(ref _hangNext, 1);
        public void Advance(long mark) => Interlocked.Exchange(ref _mark, mark);
        public void ReleaseHungCall() => _release.TrySetResult();

        public async Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            var mark = Interlocked.Read(ref _mark);
            if (Interlocked.CompareExchange(ref _hangNext, 0, 1) == 1)
            {
                _hung = true;
                await _release.Task.WaitAsync(token);
            }

            return new HighWaterStatistics
            {
                CurrentMark = mark, HighestSequence = mark, LastMark = mark, Timestamp = Timestamp
            };
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    private sealed class MarkRecorder(List<long> marks) : IObserver<ShardState>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value)
        {
            if (value.ShardName != ShardState.HighWaterMark)
            {
                return;
            }

            lock (marks)
            {
                marks.Add(value.Sequence);
            }
        }
    }
    // Answers every call with a fixed mark, except calls the test has asked to hold: each HoldNext() parks the next
    // Detect until the test releases it.
    private sealed class GatedDetector(long mark) : IHighWaterDetector
    {
        private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly object _lock = new();
        private readonly Queue<Gate> _gates = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public Uri DatabaseUri { get; } = new("fake://gated-db");
        public bool SupportsTenantPartitioning => false;

        public Gate HoldNext()
        {
            var gate = new Gate();
            lock (_lock)
            {
                _gates.Enqueue(gate);
            }

            return gate;
        }

        public async Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);

            Gate? gate = null;
            lock (_lock)
            {
                if (_gates.Count > 0)
                {
                    gate = _gates.Dequeue();
                }
            }

            if (gate != null)
            {
                await gate.WaitAsync(token);
            }

            return new HighWaterStatistics
            {
                CurrentMark = mark, HighestSequence = mark, LastMark = mark, Timestamp = Timestamp
            };
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _entered;

        public bool Entered => _entered;

        public void Release() => _released.TrySetResult();

        public Task WaitAsync(CancellationToken token)
        {
            _entered = true;
            return _released.Task.WaitAsync(token);
        }
    }

    private sealed class ActionRecorder(List<ShardAction> actions) : IObserver<ShardState>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value)
        {
            lock (actions)
            {
                actions.Add(value.Action);
            }
        }
    }
}
