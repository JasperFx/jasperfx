using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

public class CompositeProjection<TOperations, TQuerySession> : ProjectionBase, IJasperFxProjection<TOperations>, IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession> where TOperations : TQuerySession, IStorageOperations
{
    public CompositeProjection(string name)
    {
        Name = name;
        Version = 0;
        
        Stages.Add(new ProjectionStage<TOperations, TQuerySession>(1));
    }

    public override void AssembleAndAssertValidity()
    {
        base.AssembleAndAssertValidity();
    }

    protected internal List<ProjectionStage<TOperations, TQuerySession>> Stages { get; } = new();

    protected internal ProjectionStage<TOperations, TQuerySession> StageFor(int stageNumber)
    {
        if (stageNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageNumber),
                "The stages of a CompositeProjection are 1-based");
        }
        
        var stage = Stages.FirstOrDefault(x => x.Order == stageNumber);
        if (stage == null)
        {
            stage = new ProjectionStage<TOperations, TQuerySession>(stageNumber);
            Stages.Add(stage);
        }

        return stage;
    }

    bool IProjectionSource<TOperations, TQuerySession>.TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = null;
        return false;
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline()
    {
        throw new NotSupportedException("Composite Projections must run asynchronously");
    }

    public override IEnumerable<Type> PublishedTypes()
    {
        return Stages.SelectMany(stage => stage.Projections.SelectMany(x => x.PublishedTypes()));
    }

    SubscriptionType ISubscriptionSource.Type => SubscriptionType.CompositeProjection;

    ShardName[] ISubscriptionSource.ShardNames()
    {
        return [new ShardName(Name, ShardName.All, 0)];
    }

    Type ISubscriptionSource.ImplementationType => GetType();

    SubscriptionDescriptor ISubscriptionSource.Describe(IEventStore store)
    {
        var descriptor = new SubscriptionDescriptor(this, store);
        var stages = descriptor.AddChildSet(nameof(Stages));
        foreach (var stage in Stages)
        {
            stages.Rows.Add(stage.ToDescription(store));
        }
        
        return descriptor;
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var executionStages = Stages.Select(x => x.BuildExecution(store, database, loggerFactory)).ToArray();
        return new CompositeExecution<TOperations, TQuerySession>(shardName, Options, store, database, this,
            loggerFactory.CreateLogger(GetType()), executionStages);
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger, ShardName shardName)
    {
        var executionStages = Stages.Select(x => x.BuildExecution(store, database, logger)).ToArray();
        return new CompositeExecution<TOperations, TQuerySession>(shardName, Options, store, database, this,
            logger, executionStages);
    }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> ISubscriptionSource<TOperations, TQuerySession>.Shards()
    {
        var shardName = new ShardName(Name, ShardName.All, Version);
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, shardName, this, this)
        ];
    }

    Task IJasperFxProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        throw new NotSupportedException();
    }

    internal IEnumerable<IProjectionSource<TOperations, TQuerySession>> AllChildren()
    {
        return Stages.SelectMany(x => x.Projections);
    }
}