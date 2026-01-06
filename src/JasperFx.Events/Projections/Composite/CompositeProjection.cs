using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

public class CompositeProjection<TOperations, TQuerySession> : IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession> where TOperations : TQuerySession, IStorageOperations
{
    public CompositeProjection(string name)
    {
        Name = name;
    }

    private readonly List<ProjectionStage<TOperations, TQuerySession>> _stages = new();

    public IReadOnlyList<ProjectionStage<TOperations, TQuerySession>> Stages => _stages;

    public bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = null;
        return false;
    }

    public IInlineProjection<TOperations> BuildForInline()
    {
        throw new NotSupportedException("Composite Projections must run asynchronously");
    }

    public IEnumerable<Type> PublishedTypes()
    {
        return Stages.SelectMany(stage => stage.Projections.SelectMany(x => x.PublishedTypes()));
    }

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