using JasperFx.Events.Projections;
namespace JasperFx.Events.NewStuff;

public record AsyncShard<TOperations, TQuerySession>(
    AsyncOptions Options, 
    ShardRole Role, 
    ShardName Name, 
    ISubscriptionFactory<TOperations, TQuerySession> Factory, 
    EventFilterable Filters) where TOperations : TQuerySession;