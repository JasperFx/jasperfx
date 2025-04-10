using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.ContainerScoped;

/// <summary>
///     This is used to create projections that utilize scoped or transient
///     IoC services during execution
/// </summary>
/// <typeparam name="TProjection"></typeparam>
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

        ProjectionName = typeof(TProjection).Name;

        // TODO -- unit test this!
        ProjectionVersion = 1;
        if (typeof(TProjection).TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            ProjectionVersion = att.Version;
        }
    }

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

    public string Name => ProjectionName;
    public uint Version => ProjectionVersion;

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection,
                new ShardName(ProjectionName, ShardName.All, ProjectionVersion), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database,
        out IReplayExecutor executor)
    {
        executor = default;
        return false;
    }

    public IInlineProjection<TOperations> BuildForInline()
    {
        return this;
    }

    public SubscriptionDescriptor Describe()
    {
        // TODO -- some way to understand the lifecycle
        return new SubscriptionDescriptor(this, SubscriptionType.EventProjection)
        {
            Subject = ProjectionType.FullNameInCode()
        };
    }

    public Type ProjectionType { get; init; }

    public ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage,
        IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, storage, database, this,
            loggerFactory.CreateLogger<TProjection>());
    }

    public ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage,
        IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, storage, database, this,
            logger);
    }
}