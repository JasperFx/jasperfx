using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Subscriptions;

internal class ScopedSubscriptionExecution<T, TSubscription> : SubscriptionExecutionBase where T : notnull, TSubscription
{
    private readonly IServiceProvider _provider;
    private readonly ISubscriptionRunner<TSubscription>? _runner;

    public ScopedSubscriptionExecution(object storage, IServiceProvider provider, IEventDatabase database, ShardName name, ILogger logger) : base(database, name, logger)
    {
        _provider = provider;

        // jasperfx#1050: a store implements ISubscriptionRunner<ISubscription> over its own subscription
        // interface, never over the concrete subscription, and the interface is not variant. This used to
        // ask for ISubscriptionRunner<T>, which no store implements, so nothing could ever have run through
        // here. SubscriptionExecution<T> gets away with its one type parameter because it is closed over
        // that interface.
        _runner = storage as ISubscriptionRunner<TSubscription>;
        if (_runner == null)
            throw new ArgumentOutOfRangeException(nameof(storage),
                $"Must implement {typeof(ISubscriptionRunner<TSubscription>).FullNameInCode()}");
    }

    protected override async Task executeRangeAsync(IEventDatabase database, EventRange range, ShardExecutionMode mode,
        CancellationToken cancellationToken)
    {
        var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        try
        {
            var subscription = sp.GetRequiredService<T>();

            await _runner!.ExecuteAsync(subscription, database, range, mode, cancellationToken);
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

/// <summary>
/// Presents a subscription resolved from the application's IoC container to the daemon, opening a fresh
/// scope for each range of events.
/// </summary>
/// <remarks>
/// <para>
/// jasperfx#1050: public so that a store's <c>AddSubscriptionWithServices</c> can use it rather than
/// carrying its own copy. A store passes this to its <c>registerSubscription</c>, since this is not one
/// of the store's own <c>ISubscription</c> types. The execution it builds stays internal, because a store
/// only ever sees it as an <see cref="ISubscriptionExecution" />.
/// </para>
/// <para>
/// The scope runs around the store's whole <see cref="ISubscriptionRunner{T}.ExecuteAsync" />, which is
/// wider than the copies in Marten and Fisher. Theirs closed when
/// <c>ProcessEventsAsync</c> returned, before the commit and before the listener it hands back runs, so a
/// listener using anything scoped found it disposed.
/// </para>
/// </remarks>
/// <typeparam name="T">The concrete subscription, registered with the container.</typeparam>
/// <typeparam name="TOperations">The store's write session.</typeparam>
/// <typeparam name="TQuerySession">The store's read session.</typeparam>
/// <typeparam name="TSubscription">
/// The store's subscription interface: the one its <see cref="ISubscriptionRunner{T}" /> is closed over.
/// </typeparam>
public class ScopedSubscriptionServiceWrapper<T, TOperations, TQuerySession, TSubscription>: 
    EventFilterable, 
    ISubscriptionSource<TOperations, TQuerySession>, 
    ISubscriptionFactory<TOperations, TQuerySession>,
    ISubscriptionOptions
    where TOperations : TQuerySession, IStorageOperations
    where T : notnull, TSubscription
{
    private readonly IServiceProvider _provider;

    public ScopedSubscriptionServiceWrapper(IServiceProvider provider)
    {
        _provider = provider;
        Name = typeof(T).Name;
        Version = 1;

        var scope = _provider.CreateAsyncScope();

        try
        {
            var filterable = scope.ServiceProvider.GetRequiredService<T>() as EventFilterable;
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
                Version = source.Version;
                Name = source.Name;
            }
        }
        finally
        {
            // Not SafeDispose: a synchronous Dispose throws on the first scoped service that is only
            // IAsyncDisposable and abandons everything behind it, and SafeDispose then hides that.
            scope.SafeDisposeSynchronously();
        }
    }

    public uint Version { get; set; } = 1;

    public Type ImplementationType => typeof(T);

    public string Name { get; set; }
    public SubscriptionType Type => SubscriptionType.Subscription;
    public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;
    public ShardName[] ShardNames() => [ShardName.Compose(Name, version: Version)];

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new(Options, ShardRole.Subscription, ShardName.Compose(Name, version: Version), this, this)
        ];
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        return new ScopedSubscriptionExecution<T, TSubscription>(store, _provider, database, shardName,
            loggerFactory.CreateLogger<TSubscription>());
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ScopedSubscriptionExecution<T, TSubscription>(store, _provider, database, shardName,
            logger);
    }
    
    public AsyncOptions Options { get; private set; } = new();
    public SubscriptionDescriptor Describe(IEventStore store)
    {
        var descriptor = new SubscriptionDescriptor(this, store);
        descriptor.AddValue("Subscription", typeof(TSubscription).FullNameInCode());
        return descriptor;
    }
}