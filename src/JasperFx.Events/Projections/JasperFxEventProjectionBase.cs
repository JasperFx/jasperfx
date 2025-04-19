using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Descriptors;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
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
    IJasperFxProjection<TOperations> where TOperations : TQuerySession, IStorageOperations
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

        base.Name = GetType().FullNameInCode();
    }

    public string Name => base.Name!;
    public uint Version => base.Version;

    public SubscriptionType Type => SubscriptionType.EventProjection;
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];
    public Type ImplementationType => GetType();

    public virtual SubscriptionDescriptor Describe()
    {
        return new SubscriptionDescriptor(this);
    }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> ISubscriptionSource<TOperations, TQuerySession>.Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, new ShardName(Name, ShardName.All, Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)]out IReplayExecutor? executor)
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
        return applyAsync(operations, events, cancellation);
    }

    public virtual ValueTask ApplyAsync(TOperations operations, IEvent e, CancellationToken cancellation)
    {
        return _application.ApplyAsync(operations, e, cancellation);
    }

    async Task IJasperFxProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        await applyAsync(operations, events, cancellation);
    }

    private async Task applyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        foreach (var e in events)
        {
            try
            {
                await ApplyAsync(operations, e, cancellation);
            }
            catch (Exception ex)
            {
                if (ProjectionExceptions.IsExceptionTransient(ex))
                {
                    throw;  
                }
                else
                {
                    throw new ApplyEventException(e, ex);
                }
            }
        }
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, storage, database, this, logger);
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, storage, database, this, logger);
    }

    void IEntityStorage<TOperations>.Store<T>(TOperations ops, T entity)
    {
        storeEntity<T>(ops, entity);
    }

    protected abstract void storeEntity<T>(TOperations ops, T entity);
    
    public sealed override void AssembleAndAssertValidity()
    {
        if (GetType()!.GetMethod(nameof(ApplyAsync))!.DeclaringType!.Assembly != typeof(JasperFxEventProjectionBase<,>).Assembly)
        {
            if (_application.HasAnyMethods())
            {
                throw new InvalidProjectionException(
                    "Event projections can be written by either overriding the ApplyAsync() method or by using conventional methods and inline lambda registrations per event type, but not both");
            }
        }
        else
        {
            _application.AssertMethodValidity();
        }
        
    }

    [JasperFxIgnore]
    public void Project<T>(Action<T, TOperations> action) where T : class
    {
        _application.Project<T>(action);
    }

    [JasperFxIgnore]
    public void ProjectAsync<T>(Func<T, TOperations, CancellationToken, Task> action) where T : class
    {
        _application.ProjectAsync(action);
    }
}