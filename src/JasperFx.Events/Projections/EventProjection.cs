using JasperFx.Events.Daemon;
using JasperFx.Events.NewStuff;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

public abstract class EventProjection<TOperations, TQuerySession> : 
    ProjectionBase, 
    IProjectionSource<TOperations, TQuerySession>, 
    ISubscriptionFactory<TOperations, TQuerySession>,
    IInlineProjection<TOperations>,
    IEntityStorage<TOperations>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly EventProjectionApplication<TOperations> _application;
    public Type ProjectionType => GetType();

    public EventProjection()
    {
        _application = new EventProjectionApplication<TOperations>(this);
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
        return ApplyAsync(operations, events, cancellation).AsTask();
    }

    public virtual ValueTask ApplyAsync(TOperations operations, IEvent e, CancellationToken cancellation)
    {
        return _application.ApplyAsync(operations, e, cancellation);
    }

    public async ValueTask ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        // TODO -- apply one event at a time for error tracking
        foreach (var e in events)
        {
            await ApplyAsync(operations, e, cancellation);
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