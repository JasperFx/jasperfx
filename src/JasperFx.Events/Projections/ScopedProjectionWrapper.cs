#nullable enable
using JasperFx.Core;
using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Events.Projections;

/// <summary>
/// This is used to create projections that utilize scoped or transient
/// IoC services during execution
/// </summary>
/// <typeparam name="TProjection"></typeparam>
public class ScopedProjectionWrapper<TProjection, TOperations, TQuerySession> : 
    IJasperFxProjection<TOperations>, 
    IInlineProjection<TOperations>, 
    IProjectionSource<TOperations, TQuerySession>
    where TProjection : IJasperFxProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IServiceProvider _serviceProvider;

    public ScopedProjectionWrapper(IServiceProvider serviceProvider)
    {
        // TODO -- get the projection version
        
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        
        // TODO -- unit test this!
        ProjectionVersion = 1;
        if (typeof(TProjection).TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            ProjectionVersion = att.Version;
        }
    }

    // public async Task ApplyAsync(IDocumentOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    // {
    //     using var scope = _serviceProvider.CreateScope();
    //     var sp = scope.ServiceProvider;
    //     var projection = sp.GetRequiredService<TProjection>();
    //     await projection.ApplyAsync(operations, streams, cancellation).ConfigureAwait(false);
    // }

    private string _projectionName;

    public string ProjectionName
    {
        get
        {
            if (_projectionName.IsEmpty())
            {
                using var scope = _serviceProvider.CreateScope();
                var sp = scope.ServiceProvider;
                var projection = sp.GetRequiredService<TProjection>();

                if (projection is IProjectionSource<TOperations, TQuerySession> s)
                {
                    _projectionName = s.ProjectionName;
                }
                else
                {
                    var wrapper = new ProjectionWrapper<TOperations, TQuerySession>(projection, Lifecycle);
                    _projectionName = wrapper.ProjectionName;
                }
            }

            return _projectionName;
        }
        set
        {
            _projectionName = value;
        }
    }

    public async Task ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var projection = sp.GetRequiredService<TProjection>();
        await projection.ApplyAsync(operations, events, cancellation).ConfigureAwait(false);
    }

    public Task ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToList();
        return ApplyAsync(operations, events, cancellation);
    }

    public string Name => ProjectionName;
    public uint Version => ProjectionVersion;
    
    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var projection = sp.GetRequiredService<TProjection>();
        
        if (projection is IProjectionSource<TOperations, TQuerySession> s)
        {
            var shards = s.Shards();
            if (_projectionName.IsNotEmpty())
            {
                return shards.Select(x => x.OverrideProjectionName(_projectionName)).ToList();
            }
        
            return shards;
        }

        var wrapper = new ProjectionWrapper<TOperations, TQuerySession>(projection, Lifecycle){ProjectionName = _projectionName};
        return wrapper.Shards();
    }

    public bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database, out IReplayExecutor executor)
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

    public ProjectionLifecycle Lifecycle { get; set; }
    public Type ProjectionType { get; init; }

    private AsyncOptions _asyncOptions;

    [ChildDescription]
    public AsyncOptions Options
    {

        get
        {
            if (_asyncOptions == null)
            {
                using var scope = _serviceProvider.CreateScope();
                var sp = scope.ServiceProvider;
                var projection = sp.GetRequiredService<TProjection>();

                if (projection is ProjectionBase s)
                {
                    _asyncOptions = s.Options;
                }
                else
                {
                    var wrapper = new ProjectionWrapper<TOperations, TQuerySession>(projection, Lifecycle);
                    _asyncOptions = wrapper.Options;
                }
            }

            return _asyncOptions;
        }
    }

    public IEnumerable<Type> PublishedTypes()
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var projection = sp.GetRequiredService<TProjection>();

        if (projection is ProjectionBase s)
        {
            return s.PublishedTypes();
        }

        var wrapper = new ProjectionWrapper<TOperations, TQuerySession>(projection, Lifecycle);
        return wrapper.PublishedTypes();
    }

    public uint ProjectionVersion { get; set; }

}
