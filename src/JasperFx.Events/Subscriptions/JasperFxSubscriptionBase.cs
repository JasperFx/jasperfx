using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Subscriptions;

/// <summary>
/// Configuration interface for subscriptions
/// </summary>
public interface ISubscriptionOptions : IEventFilterable
{
    string Name { get; set; }
    uint Version { get; set; }
    AsyncOptions Options { get; }
}

/// <summary>
/// Base class for custom subscriptions for Marten event data
/// </summary>
public abstract class JasperFxSubscriptionBase<TOperations, TQuerySession, TSubscription>: 
    EventFilterable, 
    ISubscriptionSource<TOperations, TQuerySession>, 
    ISubscriptionFactory<TOperations, TQuerySession>,
    ISubscriptionOptions
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly TSubscription _subscription;


    protected JasperFxSubscriptionBase(TSubscription subscription)
    {
        _subscription = subscription;
        Name = subscription.GetType().NameInCode();
    }

    protected JasperFxSubscriptionBase()
    {
        _subscription = this.As<TSubscription>();
        Name = GetType().NameInCode();
    }

    public SubscriptionType Type => SubscriptionType.Subscription;
    public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];

    public Type ImplementationType => GetType();

    public virtual SubscriptionDescriptor Describe(IEventStore store)
    {
        return new SubscriptionDescriptor(this, store);
    }

    public virtual ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new(Options, ShardRole.Subscription, new ShardName(Name, ShardName.All, Version), this, this)
        ];
    }

    /// <summary>
    /// Descriptive name for Marten progress tracking and rebuild/replays
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// If this value is greater than 1, it will be treated as an all new subscription and will played from zero
    /// when deployed
    /// </summary>
    public uint Version { get; set; } = 1;

    [Obsolete("Use Name instead.")]
    public string SubscriptionName
    {
        get => Name;
        set => Name = value;
    }
    
    [Obsolete("Use Version instead.")]
    public uint SubscriptionVersion
    {
        get => Version;
        set => Version = value;
    }

    /// <summary>
    /// Fine tune the behavior of this subscription at runtime
    /// </summary>
    [ChildDescription]
    public AsyncOptions Options { get; protected set; } = new();

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return new SubscriptionExecution<TSubscription>(database, _subscription, database, shardName, logger);
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new SubscriptionExecution<TSubscription>(store, _subscription, database, shardName, logger);
    }
}

