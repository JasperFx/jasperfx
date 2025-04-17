using JasperFx.Core.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Subscriptions;

public interface ISubscriptionSource<TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
{
    string Name { get; }
    uint Version { get; }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards();
    
    AsyncOptions Options { get; }

    SubscriptionDescriptor Describe();
}