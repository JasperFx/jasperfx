using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Subscriptions;

public interface ISubscriptionFactory<TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
{
    ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database,
        ILoggerFactory loggerFactory, ShardName shardName);
}