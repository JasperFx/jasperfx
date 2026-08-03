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

    /// <summary>
    /// jasperfx#598/#610: the daemon-owned governor bounding how many shards may be inside the
    /// blue/green side-effect gate's warm-up window AND actively loading at the same time. Null =
    /// unbounded, which is the default. See
    /// <see cref="DaemonSettings.MaxConcurrentSideEffectGateWarmupsPerDatabase"/>.
    /// </summary>
    SemaphoreSlim? SideEffectGateWarmupThrottle => null;
}
