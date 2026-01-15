using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

public class CompositeExecution<TOperations, TQuerySession> : ProjectionExecution<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IReadOnlyList<ExecutionStage> _inners;

    public CompositeExecution(ShardName shardName, AsyncOptions options, IEventStore<TOperations, TQuerySession> store, IEventDatabase database, IJasperFxProjection<TOperations> projection, ILogger logger, IReadOnlyList<ExecutionStage> inners) : base(shardName, options, store, database, projection, logger)
    {
        _inners = inners;
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