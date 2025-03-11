using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Subscriptions;

// TODO -- where is this used? Can we fold it in somewhere else?
public interface ISubscriptionOptions : IEventFilterable
{
    string SubscriptionName { get; set; }
    uint SubscriptionVersion { get; set; }
    AsyncOptions Options { get; }
}

/// <summary>
/// Base class for custom subscriptions for Marten event data
/// </summary>
public abstract class SubscriptionBase<TOperations, TQuerySession, TSubscription>: 
    EventFilterable, 
    ISubscriptionSource<TOperations, TQuerySession>, 
    ISubscriptionFactory<TOperations, TQuerySession>,
    ISubscriptionOptions
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly TSubscription _subscription;


    protected SubscriptionBase(TSubscription subscription)
    {
        _subscription = subscription;
        SubscriptionName = subscription.GetType().NameInCode();
    }

    protected SubscriptionBase()
    {
        // TODO -- validate!!!
        _subscription = this.As<TSubscription>();
        SubscriptionName = GetType().NameInCode();
    }

    public virtual ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public string Name => SubscriptionName;
    public uint Version { get; } = 1;

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new(Options, ShardRole.Subscription, new ShardName(SubscriptionName, "All"), this, this)
        ];
    }

    /// <summary>
    /// Descriptive name for Marten progress tracking and rebuild/replays
    /// </summary>
    public string SubscriptionName { get; set; }

    /// <summary>
    /// If this value is greater than 1, it will be treated as an all new subscription and will played from zero
    /// when deployed
    /// </summary>
    public uint SubscriptionVersion { get; set; } = 1;

    /// <summary>
    /// Fine tune the behavior of this subscription at runtime
    /// </summary>
    [ChildDescription]
    public AsyncOptions Options { get; protected set; } = new();

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return new SubscriptionExecution<TSubscription>(database, _subscription, database, new ShardName(Name, "All"), logger);
    }
}

