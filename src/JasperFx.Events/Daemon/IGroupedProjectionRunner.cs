using JasperFx.Events.Grouping;
using JasperFx.Events.NewStuff;
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

    Task EnsureStorageExists(CancellationToken token);
    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);
}