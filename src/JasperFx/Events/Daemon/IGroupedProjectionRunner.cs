using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public interface IGroupedProjectionRunner<TBatch, TGroup> : IAsyncDisposable
 where TGroup : EventRangeGroup<TBatch>
 where TBatch : IProjectionBatch
{
    Task<TBatch> BuildBatchAsync(TGroup group);
    bool TryBuildReplayExecutor(out IReplayExecutor executor);

    ValueTask<TGroup> GroupEvents(
        EventRange range,
        CancellationToken cancellationToken);

    string ProjectionShardIdentity { get; }
    string ShardIdentity { get; }
    string DatabaseIdentifier { get; }

    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);
    Task EnsureStorageExists(CancellationToken token);
}

public class AggregationExecution<TDoc, TId> : GroupedProjectionExecution<IAggregation, TenantedSliceGroup<TDoc, TId>>
{
    public AggregationExecution(IAggregationProjectionRunner<TDoc, TId> runner, ILogger logger) : base(runner, logger)
    {
    }
}

public interface IAggregationProjectionRunner<TDoc, TId> : IGroupedProjectionRunner<IAggregation, TenantedSliceGroup<TDoc, TId>>
{
    
}
