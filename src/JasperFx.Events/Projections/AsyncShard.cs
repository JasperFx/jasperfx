using JasperFx.Events.Subscriptions;

namespace JasperFx.Events.Projections;

public record AsyncShard<TOperations, TQuerySession>(
    AsyncOptions Options, 
    ShardRole Role, 
    ShardName Name, 
    ISubscriptionFactory<TOperations, TQuerySession> Factory, 
    EventFilterable Filters) 
    where TOperations : TQuerySession, IStorageOperations
{
    public AsyncShard<TOperations, TQuerySession> OverrideProjectionName(string projectionName)
    {
        return this with { Name = new ShardName(projectionName, Name.ShardKey, Name.Version) };
    }
}