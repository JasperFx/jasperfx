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

    /// <summary>
    /// jasperfx#525: does this runner defer projected-document writes for the given range instead of writing
    /// per page? True only for an Individual aggregation batch during a rebuild when
    /// <see cref="AsyncOptions.RebuildFlushThreshold"/> is greater than 0. When true the execution accumulates
    /// across ranges and flushes at the threshold or the rebuild ceiling rather than committing per range.
    /// Defaults to false so every other runner keeps the flush-per-page behavior.
    /// </summary>
    bool DefersRebuildWrites(ShardExecutionMode mode, EventRange range) => false;

    /// <summary>
    /// jasperfx#525: the number of distinct aggregates with a pending (un-flushed) write in the current
    /// deferred-rebuild flush window. Used to decide when the threshold has been reached.
    /// </summary>
    int DeferredWriteCount => 0;

    /// <summary>
    /// jasperfx#525: is a deferred-rebuild flush due after processing <paramref name="range"/>? True when the
    /// dirty-aggregate count has reached <see cref="AsyncOptions.RebuildFlushThreshold"/>, or when the range
    /// reaches the rebuild's target ceiling (the final range), which must always flush so progress advances to
    /// the ceiling and the rebuild can complete. Defaults to false.
    /// </summary>
    bool DeferredFlushDue(EventRange range) => false;

    /// <summary>
    /// jasperfx#525: emit exactly one store/delete operation per pending aggregate into a fresh batch, execute
    /// it, advance progress to <paramref name="ceiling"/>, and open the next flush window. Aggregates written
    /// in an earlier window this rebuild are routed as UPSERTs; first-time writes may take the store's
    /// INSERT-only fast path. No-op for runners that don't defer.
    /// </summary>
    Task FlushDeferredRebuildWritesAsync(EventRange range, long ceiling, CancellationToken cancellation) =>
        Task.CompletedTask;

    /// <summary>
    /// jasperfx#525: discard the current deferred-rebuild accumulator without writing anything. Called when a
    /// shard is stopped or cancelled mid-rebuild — nothing was marked as committed, so replay is clean.
    /// </summary>
    void DiscardDeferredRebuildWrites() { }
}