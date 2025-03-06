using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.NewStuff;

public interface ISubscriptionFactory<TOperations, TQuerySession> where TOperations : TQuerySession
{
    ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory);
}


public interface ISubscriptionSource<TOperations, TQuerySession> where TOperations : TQuerySession
{
    string Name { get; }
    uint Version { get; }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards();
}