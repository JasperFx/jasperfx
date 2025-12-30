using System.Diagnostics.CodeAnalysis;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;


/*
Next steps:
Lift `processRangeAsync()` up into a public member of ICompositeMemberProjection (new interface)
Need to merge the filtering based on event types
Test EventRange clone
Flesh out CompositeTreeExecution
Regression test on DaemonTests


*/

public record ProjectionStage(ISubscriptionExecution[] Executions)
{
    public async Task ExecuteDownstreamAsync(EventRange range)
    {
        // Let's get some parallelization!!!
        var tasks = Executions.Select(execution =>
        {
            return Task.Run(async () =>
            {
                var cloned = range.Clone();
                cloned.BatchBehavior = BatchBehavior.Composite;

                await execution.ProcessRangeAsync(cloned);

                return cloned.Updates;
            });
        }).ToArray();

        var updates = await Task.WhenAll(tasks);

        // This propagates changes from upstream to downstream stages
        range.Events.InsertRange(0, updates.SelectMany(x => x.Select(o => o.ToEvent())));
    }
}

public class CompositeTreeExecution<TOperations, TQuerySession> : ProjectionExecution<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IReadOnlyList<ProjectionStage> _inners;

    public CompositeTreeExecution(ShardName shardName, AsyncOptions options, IEventStore<TOperations, TQuerySession> store, IEventDatabase database, IJasperFxProjection<TOperations> projection, ILogger logger, IReadOnlyList<ProjectionStage> inners) : base(shardName, options, store, database, projection, logger)
    {
        _inners = inners;
    }

    protected override async Task<IProjectionBatch> buildBatchWithNoSkippingAsync(EventRange range, CancellationToken cancellationToken)
    {
        IProjectionBatch<TOperations, TQuerySession>? batch = null;
        try
        {
            // TODO -- will need to also mark progress too!
            
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
