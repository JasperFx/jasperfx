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

    /// <summary>
    /// The store-wide gate bounding how many projection batches are held in memory at once, from the
    /// start of the build until the batch is committed or abandoned. Shared by every daemon of the store
    /// in this process. Null = unbounded. See <see cref="DaemonSettings.MaxConcurrentBatchesInFlightPerNode"/>.
    /// </summary>
    SemaphoreSlim? BatchesInFlightThrottle => null;
}
