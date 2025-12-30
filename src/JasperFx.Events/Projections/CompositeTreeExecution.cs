using System.Diagnostics.CodeAnalysis;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;


public class CompositeTreeExecution<TOperations, TQuerySession> : ProjectionExecution<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IReadOnlyList<ISubscriptionExecution> _inners;

    public CompositeTreeExecution(ShardName shardName, AsyncOptions options, IEventStore<TOperations, TQuerySession> store, IEventDatabase database, IJasperFxProjection<TOperations> projection, ILogger logger, IReadOnlyList<ISubscriptionExecution> inners) : base(shardName, options, store, database, projection, logger)
    {
        _inners = inners;
    }

    protected override async Task<IProjectionBatch> buildBatchAsync(EventRange range, CancellationToken cancellationToken)
    {
        IProjectionBatch<TOperations, TQuerySession>? batch = null;
        try
        {
            batch = await _store.StartProjectionBatchAsync(range, _database, Mode, _options, cancellationToken);
            
            foreach (var inner in _inners)
            {
                var cloned = range.Clone();
                cloned.BatchBehavior = BatchBehavior.Composite;
                cloned.ActiveBatch = batch;

                await inner.ProcessRangeAsync(cloned);
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

/*
Next steps:
Lift `processRangeAsync()` up into a public member of ICompositeMemberProjection (new interface)
Need to merge the filtering based on event types
Test EventRange clone
Flesh out CompositeTreeExecution
Regression test on DaemonTests


*/