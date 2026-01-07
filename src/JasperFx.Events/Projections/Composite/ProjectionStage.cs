using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

public class ProjectionStage<TOperations, TQuerySession>(int order)
    where TOperations : TQuerySession, IStorageOperations
{
    public int Order { get; } = order;

    private readonly List<IProjectionSource<TOperations, TQuerySession>> _projections = new();

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

    public void Add(IProjectionSource<TOperations, TQuerySession> projection) => _projections.Add(projection);

    public IReadOnlyList<IProjectionSource<TOperations, TQuerySession>> Projections => _projections;

    public ExecutionStage BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory)
    {
        var executions = Projections.Select(projection =>
        {
            var shardName = new ShardName(projection.Name, ShardName.All, projection.Version);
            return projection.As<ISubscriptionFactory<TOperations, TQuerySession>>()
                .BuildExecution(store, database, loggerFactory, shardName);
        }).ToArray();

        return new ExecutionStage(executions);
    }

    public ExecutionStage BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger) 
    {
        var executions = Projections.Select(projection =>
        {
            var shardName = new ShardName(projection.Name, ShardName.All, projection.Version);
            return projection.As<ISubscriptionFactory<TOperations, TQuerySession>>()
                .BuildExecution(store, database, logger, shardName);
        }).ToArray();

        return new ExecutionStage(executions);
    }
}