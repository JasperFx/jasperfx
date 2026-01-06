using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

public class CompositeProjection<TOperations, TQuerySession> : ProjectionBase, IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession> where TOperations : TQuerySession, IStorageOperations
{
    public CompositeProjection(string name)
    {
        Name = name;
        Version = 0;
        
        _stages.Add(new ProjectionStage<TOperations, TQuerySession>(1));
    }

    public override void AssembleAndAssertValidity()
    {
        base.AssembleAndAssertValidity();
    }

    private readonly List<ProjectionStage<TOperations, TQuerySession>> _stages = new();

    public IReadOnlyList<ProjectionStage<TOperations, TQuerySession>> Stages => _stages;

    public ProjectionStage<TOperations, TQuerySession> LastStage => _stages.Last();

    public ProjectionStage<TOperations, TQuerySession> AddStage()
    {
        _stages.Add(new ProjectionStage<TOperations, TQuerySession>(_stages.Count + 1));
        return _stages.Last();
    }

    /// <summary>
    /// Try to find an existing stage by its *1-based* order
    /// </summary>
    /// <param name="stageNumer"></param>
    /// <param name="stage"></param>
    /// <returns></returns>
    public bool TryFind(int stageNumber, [NotNullWhen(true)] out ProjectionStage<TOperations, TQuerySession>? stage)
    {
        stage = _stages.FirstOrDefault(x => x.Order == stageNumber);
        return stage != null;
    }

    public bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = null;
        return false;
    }

    public IInlineProjection<TOperations> BuildForInline()
    {
        throw new NotSupportedException("Composite Projections must run asynchronously");
    }

    public override IEnumerable<Type> PublishedTypes()
    {
        return Stages.SelectMany(stage => stage.Projections.SelectMany(x => x.PublishedTypes()));
    }

    public SubscriptionType Type => SubscriptionType.CompositeProjection;
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
}