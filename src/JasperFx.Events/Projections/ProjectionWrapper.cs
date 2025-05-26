using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

public class ProjectionWrapper<TOperations, TQuerySession> : 
    ProjectionBase, 
    IProjectionSource<TOperations, TQuerySession>, 
    ISubscriptionFactory<TOperations, TQuerySession>,
    IInlineProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IJasperFxProjection<TOperations> _projection;

    public ProjectionWrapper(IJasperFxProjection<TOperations> projection, ProjectionLifecycle lifecycle)
    {
        _projection = projection;
        Lifecycle = lifecycle;
        base.Name = projection.GetType().Name;

        Inner = _projection;

        // TODO -- unit test this!
        base.Version = 1;
        if (_projection.GetType().TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            base.Version = att.Version;
        }

        if (projection is ISubscriptionSource subscriptionSource)
        {
            Type = subscriptionSource.Type;
        }
        else
        {
            Type = SubscriptionType.EventProjection;
        }

        if (projection is ProjectionBase source)
        {
            // TODO -- Unit test all of this in JasperFx.Events
            base.Name = source.Name;
            base.Version = source.Version;
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
    }

    public SubscriptionType Type { get; }
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];
    public Type ImplementationType => _projection.GetType();

    public override string ToString()
    {
        return $"{_projection}, {nameof(Name)}: {Name}, {nameof(Version)}: {Version}";
    }

    public SubscriptionDescriptor Describe()
    {
        return new SubscriptionDescriptor(this);
    }

    [ChildDescription]
    public IJasperFxProjection<TOperations> Inner { get; }

    public Type ProjectionType => _projection.GetType();

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, store, database, _projection, logger);
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, store, database, _projection, logger);
    }

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new(Options, ShardRole.Projection, new ShardName(base.Name, "All", Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)]out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    public IInlineProjection<TOperations> BuildForInline()
    {
        return this;
    }
    
    Task IInlineProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToList();
        return _projection.ApplyAsync(operations, events, cancellation);
    }
}
