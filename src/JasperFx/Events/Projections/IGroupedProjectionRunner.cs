namespace JasperFx.Events.Projections;

public interface IGroupedProjectionRunner<TBatch, TDatabase, TGroup> : IAsyncDisposable
 where TGroup : EventRangeGroup<TBatch, TDatabase>
 where TBatch : IProjectionBatch
{
    Task<TBatch> BuildBatchAsync(TGroup group);
    bool TryBuildReplayExecutor(out IReplayExecutor executor);

    ValueTask<TGroup> GroupEvents(
        EventRange range,
        CancellationToken cancellationToken);

    TDatabase Database { get; }
    string ProjectionShardIdentity { get; }
    string ShardIdentity { get; }
    string DatabaseIdentifier { get; }

    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);
    Task EnsureStorageExists(CancellationToken token);
}
