using JasperFx.Core.Descriptors;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Subscriptions;

public interface ISubscriptionSource
{
    string Name { get; }
    
    /// <summary>
    /// Specify that this projection is a non 1 version of the original projection definition to opt
    /// into the event store's parallel blue/green deployment of this projection.
    /// </summary>
    uint Version { get; }
    
    SubscriptionType Type { get; }
    
    ProjectionLifecycle Lifecycle { get; }

    ShardName[] ShardNames();
    
    /// <summary>
    /// The actual type that implements this subscription or projection
    /// </summary>
    Type ImplementationType { get; }
}

public interface ISubscriptionSource<TOperations, TQuerySession> : ISubscriptionSource
    where TOperations : TQuerySession, IStorageOperations
{
    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards();
    
    AsyncOptions Options { get; }

    // TODO - See if this is unnecessary now that there's more logic in SubscriptionDescriptor?
    SubscriptionDescriptor Describe();
}