using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public enum SliceBehavior
{
    None,
    Preprocess,
    JustInTime
}

public interface IGroupedProjectionRunner : IAsyncDisposable
{
    SliceBehavior SliceBehavior { get; }

    Task<IProjectionBatch> BuildBatchAsync(EventRange range, ShardExecutionMode mode, CancellationToken cancellation);
    bool TryBuildReplayExecutor(out IReplayExecutor executor);

    IEventSlicer Slicer { get; }

    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);

    /// <summary>
    /// Apply CacheLimitPerTenant to any in-memory aggregate caches owned by this runner.
    /// Composite projections defer this until all stages have run so that downstream stages
    /// can read in-flight upstream entities without losing them to mid-batch eviction.
    /// </summary>
    Task CompactCachesAsync() => Task.CompletedTask;
}