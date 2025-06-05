using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.ContainerScoped;

/// <summary>
///     This is used to create projections that utilize scoped or transient
///     IoC services during execution
/// </summary>
/// <typeparam name="TProjection"></typeparam>
/// <typeparam name="TOperations"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
public class ScopedProjectionWrapper<TProjection, TOperations, TQuerySession> : ProjectionBase,
    IJasperFxProjection<TOperations>,
    IInlineProjection<TOperations>,
    IProjectionSource<TOperations, TQuerySession>,
    ISubscriptionFactory<TOperations, TQuerySession> where TProjection : IJasperFxProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IServiceProvider _serviceProvider;

    public ScopedProjectionWrapper(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        base.Name = typeof(TProjection).Name;

        ProjectionType = typeof(TProjection);

        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var raw = sp.GetRequiredService<TProjection>();

        if (raw is ISubscriptionSource subscriptionSource)
        {
            Type = subscriptionSource.Type;
        }
        else
        {
            Type = SubscriptionType.EventProjection;
        }
        
        if (raw is ProjectionBase source)
        {
            base.Name = source.Name;
            base.Version = source.Version;

            replaceOptions(source.Options);

            if (source is EventFilterable filterable)
            {
                StreamType = filterable.StreamType;
                IncludedEventTypes.AddRange(filterable.IncludedEventTypes);
                IncludeArchivedEvents = filterable.IncludeArchivedEvents;
            }

            foreach (var publishedType in source.PublishedTypes())
            {
                RegisterPublishedType(publishedType);
            }
        }
        
        base.Version = 1;
        if (typeof(TProjection).TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            base.Version = att.Version;
        }

    }

    public SubscriptionType Type { get; }
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];
    public Type ImplementationType => typeof(TProjection);

    public Task ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToList();
        return ApplyAsync(operations, events, cancellation);
    }

    public async Task ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var projection = sp.GetRequiredService<TProjection>();
        await projection.ApplyAsync(operations, events, cancellation).ConfigureAwait(false);
    }

    public new string Name => base.Name;
    public new uint Version => base.Version;

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection,
                new ShardName(base.Name, ShardName.All, base.Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        [NotNullWhen(true)]out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    public IInlineProjection<TOperations> BuildForInline()
    {
        return this;
    }

    public SubscriptionDescriptor Describe(IEventStore store)
    {
        // TODO -- some way to understand the lifecycle
        return new SubscriptionDescriptor(this, store)
        {
            Subject = ProjectionType.FullNameInCode()
        };
    }

    public Type ProjectionType { get; init; }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, store, database, this,
            loggerFactory.CreateLogger<TProjection>());
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, store, database, this,
            logger);
    }
}