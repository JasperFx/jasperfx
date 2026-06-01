using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

public class CompositeExecution<TOperations, TQuerySession> : ProjectionExecution<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IReadOnlyList<ExecutionStage> _inners;
    private readonly EventFilterable _filtering;
    private readonly bool _replayEligible;

    public CompositeExecution(ShardName shardName, AsyncOptions options, IEventStore<TOperations, TQuerySession> store, IEventDatabase database, IJasperFxProjection<TOperations> projection, ILogger logger, IReadOnlyList<ExecutionStage> inners, bool replayEligible = true) : base(shardName, options, store, database, projection, logger)
    {
        _inners = inners;
        _replayEligible = replayEligible;

        // The composite projection is itself an EventFilterable; the replay loader uses it to scope the
        // single pass the same way the continuous shard does.
        _filtering = projection as EventFilterable ?? new EventFilterable();
    }

    public override bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = null;
        if (!_replayEligible)
        {
            return false;
        }

        var loader = _store.BuildEventLoader(_database, _logger, _filtering, _options);
        executor = new CompositeReplayExecutor(_shardName, loader, this, _database, _options, _logger);
        return true;
    }

    protected override async Task<IProjectionBatch> buildBatchWithNoSkippingAsync(EventRange range, CancellationToken cancellationToken)
    {
        IProjectionBatch<TOperations, TQuerySession>? batch = null;
        try
        {
            batch = await _store.StartProjectionBatchAsync(range, _database, Mode, _options, cancellationToken);

            range.ActiveBatch = batch;

            foreach (var stage in _inners)
            {
                await stage.ExecuteDownstreamAsync(range);
            }

            // Compact each child execution's aggregate caches now that every stage has run.
            // BuildBatchAsync skipped per-stage compaction because downstream stages need to
            // read the upstream's in-flight entities; we do it once here at the composite
            // boundary instead. See JasperFx/marten#4329.
            foreach (var stage in _inners)
            {
                foreach (var execution in stage.Executions)
                {
                    await execution.CompactCachesAsync();
                }
            }

            return batch;
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Subscription {Name} failed while creating a SQL batch for updates for events from {Floor} to {Ceiling}",
                _shardName.Identity, range.SequenceFloor, range.SequenceCeiling);

            if (batch != null)
            {
                await batch!.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
}
