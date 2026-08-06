using JasperFx.Events.Daemon.HighWater;

namespace JasperFx.Events.TestSupport;

/// <summary>
/// An in-process <see cref="IDaemonWakeup"/> for <see cref="ProjectionScenario{TOperations,TQuerySession}"/>.
///
/// <para>A scenario owns both ends of the problem — it appends the events AND it runs the daemon that
/// must notice them — so it can signal the high-water agent directly. No database round trip and no
/// LISTEN/NOTIFY: the wake is a semaphore release in the same process.</para>
///
/// <para>Without it a scenario pays <c>SlowPollingTime</c> (1s by default) at every batch boundary. The
/// harness wipes the event store and then starts the daemon, so the agent's first look sees an empty
/// store and settles into the slow interval; and after each batch drains, <c>CurrentMark ==
/// HighestSequence</c> puts it right back there. Every append then races a sleeping agent. See marten#5195.</para>
/// </summary>
internal sealed class ScenarioDaemonWakeup: IDaemonWakeup
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>
    /// Wake the high-water agent now. Safe to call from any thread and safe to call when nothing is waiting —
    /// the signal is sticky, so an append that lands between two waits still wakes the following one.
    /// </summary>
    public void Pulse()
    {
        // Cap the pending signal at one. A burst of appends does not need a wake apiece: the agent re-reads
        // the real high-water mark on every cycle, so a single wake already covers everything committed so
        // far, and queueing more would only spin the loop against an unchanged sequence. CurrentCount is a
        // racy read, hence the SemaphoreFullException guard rather than a lock.
        if (_signal.CurrentCount != 0) return;

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another thread pulsed between the check and the release. One pending wake is all we wanted.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        // False means the timeout elapsed with no append, which is the ordinary idle path -- the agent polls
        // as it always would. True means an append woke us early, which is the whole point.
        await _signal.WaitAsync(timeout, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Deliberately does NOT dispose the semaphore. The high-water agent's poll loop may still be sitting
        // in WaitAsync when the scenario tears down, and disposing underneath it would surface as an
        // ObjectDisposedException inside the loop. SemaphoreSlim holds no unmanaged resource unless
        // AvailableWaitHandle is touched, which this type never does, so there is nothing to leak.
    }
}
