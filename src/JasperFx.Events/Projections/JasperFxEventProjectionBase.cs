using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

/// <summary>
/// Base class for adhoc projections
/// </summary>
/// <typeparam name="TOperations"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
public abstract class JasperFxEventProjectionBase<TOperations, TQuerySession> : 
    ProjectionBase, 
    IProjectionSource<TOperations, TQuerySession>, 
    ISubscriptionFactory<TOperations, TQuerySession>,
    IInlineProjection<TOperations>,
    IEntityStorage<TOperations>,
    IProjection<TOperations> where TOperations : TQuerySession, IStorageOperations
{
    private readonly EventProjectionApplication<TOperations> _application;
    public Type ProjectionType => GetType();

    public JasperFxEventProjectionBase()
    {
        _application = new EventProjectionApplication<TOperations>(this);
        
        IncludedEventTypes.Fill(_application.AllEventTypes());

        foreach (var publishedType in _application.PublishedTypes())
        {
            RegisterPublishedType(publishedType);
        }

        ProjectionName = GetType().FullNameInCode();
    }

    // TODO -- rename these? Or leave them alone?
    public string Name => ProjectionName!;
    public uint Version => ProjectionVersion;
    
    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        // TODO -- this *will* get fancier if we do the async projection sharding
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, new ShardName(Name), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database, out IReplayExecutor executor)
    {
        executor = default;
        return false;
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline()
    {
        return this;
    }

    Task IInlineProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToList();
        return ApplyAsync(operations, events, cancellation);
    }

    public virtual ValueTask ApplyAsync(TOperations operations, IEvent e, CancellationToken cancellation)
    {
        return _application.ApplyAsync(operations, e, cancellation);
    }

    public async Task ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        // TODO -- apply one event at a time for error tracking, watch what is and is not a transient exception
        foreach (var e in events)
        {
            try
            {
                await ApplyAsync(operations, e, cancellation);
            }
            catch (Exception ex)
            {
                // TODO -- check if is transient, and if now, throw ApplyEventException
                throw;  
            }
        }
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, storage, database, this, logger);
    }

    public void Store<T>(TOperations ops, T entity)
    {
        storeEntity<T>(ops, entity);
    }

    protected abstract void storeEntity<T>(TOperations ops, T entity);
}