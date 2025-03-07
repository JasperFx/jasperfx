using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.NewStuff;

public interface ISubscriptionFactory<TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
{
    ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database,
        ILoggerFactory loggerFactory, ShardName shardName);
}


public interface ISubscriptionSource<TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
{
    string Name { get; }
    uint Version { get; }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards();
}