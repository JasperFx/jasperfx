namespace JasperFx.Events.Daemon;

internal static class BatchWriteThrottleExtensions
{
    /// <summary>
    /// Release the shared batch-write throttle from a batch execution's <c>finally</c> without letting an
    /// <see cref="ObjectDisposedException"/> escape. The throttle is owned by the daemon
    /// (<c>JasperFxAsyncDaemon._batchWriteThrottle</c>) and disposed on shutdown; an in-flight batch can
    /// reach its release after that disposal and race it. A disposed throttle means the daemon is already
    /// gone, so the release is a no-op rather than a fault (jasperfx#557).
    /// </summary>
    public static void SafeRelease(this SemaphoreSlim? throttle)
    {
        try
        {
            throttle?.Release();
        }
        catch (ObjectDisposedException)
        {
            // The daemon disposed the shared throttle out from under this unwinding batch — nothing to release.
        }
    }
}
