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

    /// <summary>
    /// Apply the aggregate-cache mutations produced by the most recent batch build now that the
    /// batch has successfully committed. Caching is "populate on commit": a build that throws or a
    /// batch that fails to commit must NOT leave mutated aggregates in the cache, otherwise a
    /// retry/skip rebuild would re-apply events on top of an already-updated cached aggregate
    /// (see marten#4730). No-op for runners that don't keep a cache.
    /// </summary>
    void ApplyPendingCacheUpdates() { }
}