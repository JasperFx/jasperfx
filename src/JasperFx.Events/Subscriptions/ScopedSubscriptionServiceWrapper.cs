using JasperFx.Core;
using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Subscriptions;

internal class ScopedSubscriptionExecution<T, TSubscription> : SubscriptionExecutionBase where T : TSubscription
{
    private readonly IServiceProvider _provider;
    private readonly ISubscriptionRunner<T> _runner;

    public ScopedSubscriptionExecution(object storage, IServiceProvider provider, IEventDatabase database, ShardName name, ILogger logger) : base(database, name, logger)
    {
        _provider = provider;
        _runner = storage as ISubscriptionRunner<T>;
        if (_runner == null)
            throw new ArgumentOutOfRangeException(nameof(storage),
                $"Must implement {typeof(ISubscriptionRunner<T>).FullNameInCode()}");


    }

    protected override async Task executeRangeAsync(IEventDatabase database, EventRange range, ShardExecutionMode mode,
        CancellationToken cancellationToken)
    {
        var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        try
        {
            var subscription = sp.GetRequiredService<T>();

            await _runner.ExecuteAsync(subscription, database, range, mode, cancellationToken);
        }
        finally
        {
            if (scope is IAsyncDisposable ad)
            {
                await ad.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                scope.SafeDispose();
            }
        }
    }
}

internal class ScopedSubscriptionServiceWrapper<T, TOperations, TQuerySession, TSubscription>: 
    EventFilterable, 
    ISubscriptionSource<TOperations, TQuerySession>, 
    ISubscriptionFactory<TOperations, TQuerySession>,
    ISubscriptionOptions
    where TOperations : TQuerySession, IStorageOperations
    where T : TSubscription
{
    private readonly IServiceProvider _provider;

    public ScopedSubscriptionServiceWrapper(IServiceProvider provider)
    {
        _provider = provider;
        SubscriptionName = typeof(T).Name;

        var scope = _provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var filterable = sp.GetRequiredService<T>() as EventFilterable;
        if (filterable != null)
        {
            IncludedEventTypes.AddRange(filterable.IncludedEventTypes);
            StreamType = filterable.StreamType;
            IncludeArchivedEvents = filterable.IncludeArchivedEvents;
        }

        // TODO -- might need to do this differently to get at options a little easier
        if (filterable is ISubscriptionSource<TOperations, TQuerySession> source)
        {
            Options = source.Options;
        }
        
        scope.SafeDispose();
    }

    string ISubscriptionSource<TOperations, TQuerySession>.Name => SubscriptionName;
    uint ISubscriptionSource<TOperations, TQuerySession>.Version => SubscriptionVersion;
    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new(Options, ShardRole.Subscription, new ShardName(SubscriptionName, "All"), this, this)
        ];
    }

    public ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        return new ScopedSubscriptionExecution<T, TSubscription>(storage, _provider, database, shardName,
            loggerFactory.CreateLogger(typeof(TSubscription)));
    }

    public ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ScopedSubscriptionExecution<T, TSubscription>(storage, _provider, database, shardName,
            logger);
    }

    public string SubscriptionName { get; set; }
    public uint SubscriptionVersion { get; set; }
    public AsyncOptions Options { get; private set; } = new();
    public SubscriptionDescriptor Describe()
    {
        var descriptor = new SubscriptionDescriptor(this, SubscriptionType.Subscription);
        descriptor.AddValue("Subscription", typeof(TSubscription).FullNameInCode());
        return descriptor;
    }
}