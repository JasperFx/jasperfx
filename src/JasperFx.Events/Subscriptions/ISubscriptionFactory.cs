using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Subscriptions;

public interface ISubscriptionFactory<TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
{
    ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        ILoggerFactory loggerFactory, ShardName shardName);

    ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        ILogger logger, ShardName shardName);
}