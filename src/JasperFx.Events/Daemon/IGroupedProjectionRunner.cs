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
    
    Task<IProjectionBatch> BuildBatchAsync(EventRange range);
    bool TryBuildReplayExecutor(out IReplayExecutor executor);

    IEventSlicer Slicer { get; }

    string ProjectionShardIdentity { get; }
    string ShardIdentity { get; }
    string DatabaseIdentifier { get; }

    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);
    Task EnsureStorageExists(CancellationToken token);
}
