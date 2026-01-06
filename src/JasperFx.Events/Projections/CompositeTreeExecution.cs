using System.Diagnostics.CodeAnalysis;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

public class CompositeProjection<TOperations, TQuerySession> : ISubscriptionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession> where TOperations : TQuerySession, IStorageOperations
{
    public CompositeProjection(string name)
    {
        Name = name;
    }

    private readonly List<ProjectionStage<TOperations, TQuerySession>> _stages = new();

    public IReadOnlyList<ProjectionStage<TOperations, TQuerySession>> Stages => _stages;
    
    public string Name { get; }
    public uint Version => 0;
    public SubscriptionType Type => SubscriptionType.CompositeProjection;
    public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;
    public ShardName[] ShardNames()
    {
        return [new ShardName(Name, ShardName.All, 0)];
    }

    public Type ImplementationType => GetType();
    public SubscriptionDescriptor Describe(IEventStore store)
    {
        var descriptor = new SubscriptionDescriptor(this, store);
        var stages = descriptor.AddChildSet(nameof(Stages));
        foreach (var stage in _stages)
        {
            stages.Rows.Add(stage.ToDescription(store));
        }
        
        return descriptor;
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        throw new NotImplementedException();
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger, ShardName shardName)
    {
        throw new NotImplementedException();
    }

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        throw new NotImplementedException();
    }

    public AsyncOptions Options { get; } = new();
}

public class ProjectionStage<TOperations, TQuerySession>(int Order)
    where TOperations : TQuerySession, IStorageOperations
{
    public OptionsDescription ToDescription(IEventStore store)
    {
        var description = new OptionsDescription(this);
        var @set = description.AddChildSet("Projections");

        foreach (var projection in Projections)
        {
            @set.Rows.Add(projection.Describe(store));
        }

        return description;
    }

    public List<ISubscriptionSource<TOperations, TQuerySession>> Projections { get; } = [];
}

public record ExecutionStage(ISubscriptionExecution[] Executions)
{
    public async Task ExecuteDownstreamAsync(EventRange range)
    {
        // Let's get some parallelization!!!
        var tasks = Executions.Select(execution =>
        {
            return Task.Run(async () =>
            {
                var cloned = range.CloneForExecutionLeaf(execution.ShardName);
                cloned.BatchBehavior = BatchBehavior.Composite;

                // Need to record the individual progress even though it's locked together
                await cloned.ActiveBatch!.RecordProgress(cloned);
                
                await execution.ProcessRangeAsync(cloned);
                
                // This allows us to propagate the aggregate cache data to
                // downstream aggregations
                range.Upstream.Add(execution);

                return cloned.Updates;
            });
        }).ToArray();

        var updates = await Task.WhenAll(tasks);

        // This propagates changes from upstream to downstream stages
        range.Events.InsertRange(0, updates.SelectMany(x => x.Select(o => o.ToEvent())));
    }
}

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
            await batch.RecordProgress(range);
            
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
