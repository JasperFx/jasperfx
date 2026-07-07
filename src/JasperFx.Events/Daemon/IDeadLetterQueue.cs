using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public interface IDaemonRuntime
{
    Task RecordDeadLetterEventAsync(DeadLetterEvent @event);
    ILogger Logger { get; }
    long HighWaterMark();

    /// <summary>
    /// Epic #486 WS3: the daemon-owned governor bounding concurrent projection batch
    /// execute/commit sessions against this daemon's database. Null = unbounded. See
    /// <see cref="DaemonSettings.MaxConcurrentBatchWritesPerDatabase"/>.
    /// </summary>
    SemaphoreSlim? BatchWriteThrottle => null;
}
