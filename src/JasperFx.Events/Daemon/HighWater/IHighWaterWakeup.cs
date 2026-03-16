namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
/// Abstraction over the delay mechanism used by the <see cref="HighWaterAgent"/>
/// between polling cycles. Implementations can use external signals (e.g. PostgreSQL
/// LISTEN/NOTIFY) to wake the agent immediately when new events are appended,
/// instead of waiting for the full polling interval to elapse.
/// </summary>
public interface IHighWaterWakeup : IDisposable
{
    /// <summary>
    /// Wait for either an external notification or the specified timeout to elapse,
    /// whichever comes first.
    /// </summary>
    /// <param name="timeout">Maximum time to wait before returning.</param>
    /// <param name="token">Cancellation token.</param>
    Task WaitAsync(TimeSpan timeout, CancellationToken token);
}

/// <summary>
/// Default implementation that simply delays for the full timeout.
/// This preserves the existing polling behavior.
/// </summary>
internal class TaskDelayWakeup : IHighWaterWakeup
{
    public Task WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        return Task.Delay(timeout, token);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
