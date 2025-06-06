using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.ContainerScoped;

/// <summary>
/// Scoped wrapper for IProjectionSource projections that are not aggregations
/// </summary>
/// <typeparam name="TSource"></typeparam>
/// <typeparam name="TOperations"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
public abstract class ProjectionSourceWrapperBase<TSource, TOperations, TQuerySession> : 
    ProjectionBase, 
    IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession>,
    IInlineProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
    where TSource : IProjectionSource<TOperations, TQuerySession>
{
    protected readonly IServiceProvider _serviceProvider;
    private readonly Type[] _publishedTypes;

    public ProjectionSourceWrapperBase(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var source = sp.GetRequiredService<TSource>();

        base.Name = source.Name;
        base.Version = source.Version;
        ProjectionType = source.ImplementationType;
        Type = source.Type;
        Name = source.Name;
        Version = source.Version;

        replaceOptions(source.Options);

        if (source is EventFilterable filterable)
        {
            StreamType = filterable.StreamType;
            IncludedEventTypes.AddRange(filterable.IncludedEventTypes);
            IncludeArchivedEvents = filterable.IncludeArchivedEvents;
        }

        // ReSharper disable once VirtualMemberCallInConstructor
        configureWithSource(source);

        _publishedTypes = source.PublishedTypes().ToArray();
    }

    protected virtual void configureWithSource(TSource source)
    {
        // nothing
    }

    public SubscriptionType Type { get; private set; }
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];
    public Type ImplementationType => typeof(TSource);

    public Type ProjectionType { get; }

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, new ShardName(base.Name, ShardName.All, Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        [NotNullWhen(true)]out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    public abstract ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName);

    public abstract ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName);

    public async Task ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var source = sp.GetRequiredService<TSource>();
        var projection = source.BuildForInline();
        await projection.ApplyAsync(operations, streams, cancellation).ConfigureAwait(false);
    }

    SubscriptionDescriptor ISubscriptionSource.Describe(IEventStore store)
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var source = sp.GetRequiredService<TSource>();
        var description = source.Describe(store);
        return description;
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline()
    {
        return this;
    }

    IEnumerable<Type> IProjectionSource<TOperations, TQuerySession>.PublishedTypes()
    {
        return _publishedTypes.Concat(Options.StorageTypes).Distinct().ToArray();
    }

}