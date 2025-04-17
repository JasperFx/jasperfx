using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Descriptors;
using JasperFx.Events.Daemon;
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
    private readonly SubscriptionDescriptor _description;

    public ProjectionSourceWrapperBase(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var source = sp.GetRequiredService<TSource>();

        ProjectionName = source.ProjectionName;
        ProjectionVersion = source.ProjectionVersion;
        ProjectionType = source.ProjectionType;
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

        _description = source.Describe();

        _publishedTypes = source.PublishedTypes().ToArray();
    }

    protected virtual void configureWithSource(TSource source)
    {
        // nothing
    }

    public Type ProjectionType { get; }
    public string Name { get; }
    public uint Version { get; }
    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, new ShardName(ProjectionName, ShardName.All, Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database,
        [NotNullWhen(true)]out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    public abstract ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName);

    public abstract ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILogger logger,
        ShardName shardName);

    public async Task ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var source = sp.GetRequiredService<TSource>();
        var projection = source.BuildForInline();
        await projection.ApplyAsync(operations, streams, cancellation).ConfigureAwait(false);
    }

    SubscriptionDescriptor ISubscriptionSource<TOperations, TQuerySession>.Describe()
    {
        return _description;
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline()
    {
        return this;
    }

    IEnumerable<Type> IProjectionSource<TOperations, TQuerySession>.PublishedTypes()
    {
        return _publishedTypes;
    }

}