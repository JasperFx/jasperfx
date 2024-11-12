using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Projections;

/*
 * Notes
 * Valid return types are void & Task
 * Valid argument types are the TOperations, IEvent, IEvent<T>, and the event type. CancellationToken
 *
 *
 * Use TypeOf, IfThen
 */


public enum TenancyBehavior
{
    /// <summary>
    /// This projection will process events for all tenants at one time,
    /// with the user being responsible for tenanted operations
    /// </summary>
    AcrossTenants,
    
    /// <summary>
    /// This projection will process events for one tenant at a time
    /// </summary>
    ByTenant
}

/// <summary>
/// Base class for creating adhoc projections
/// </summary>
public abstract class ProjectionSource<TOperations, TStore, TDatabase> : ProjectionBase, IProjectionSource<TStore, TDatabase>
{
    protected ProjectionSource()
    {
        ProjectionName = GetType().FullNameInCode();
    }

    public override void AssembleAndAssertValidity()
    {
        // TODO -- validate this a bit
        base.AssembleAndAssertValidity();
    }

    /// <summary>
    /// In the case of multi-tenancy, allow this projection to be applied to all tenants. Default
    /// is one tenant at a time
    /// </summary>
    public TenancyBehavior TenancyBehavior { get; set; } = TenancyBehavior.ByTenant;

    public bool TryBuildReplayExecutor(TStore store, TDatabase database, out IReplayExecutor executor)
    {
        executor = default;
        return false;
    }

    // TODO -- watch this. Won't be right later
    public Type ProjectionType => GetType();
    public AsyncOptions Options { get; } = new();
    
    public IReadOnlyList<IAsyncShard<TDatabase>> AsyncProjectionShards()
    {
        throw new NotImplementedException();
    }

    string ISubscriptionSource<TStore, TDatabase>.Name => ProjectionName;
    uint ISubscriptionSource<TStore, TDatabase>.Version => ProjectionVersion;
    
    /// <summary>
    /// Override this method to implement a custom, adhoc projection
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="events"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    public virtual Task ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        // TODO -- make this use the Project<T> stuff. Order by most specific first
        return Task.CompletedTask;
    }
    
    [JasperFxIgnore]
    public void Project<TEvent>(Action<TEvent, TOperations> project)
    {
        //_projectMethods.AddLambda(project, typeof(TEvent));
    }

    [JasperFxIgnore]
    public void ProjectAsync<TEvent>(Func<TEvent, TOperations, CancellationToken, Task> project)
    {
        //_projectMethods.AddLambda(project, typeof(TEvent));
    }
}

internal interface IEventHandler<TOperations>
{
    Type EventType { get; }
}

internal class SyncLambdaEventHandler<TOperations, TEvent> : IEventHandler<TOperations>
{
    private readonly Action<TEvent, TOperations> _project;

    public SyncLambdaEventHandler(Action<TEvent, TOperations> project)
    {
        _project = project;
    }

    public Type EventType => typeof(TEvent).UnwrapEventType();
}

internal class AsyncLambdaEventHandler<TOperations, TEvent> : IEventHandler<TOperations>
{
    private readonly Func<TEvent, TOperations, CancellationToken, Task> _project;

    public AsyncLambdaEventHandler(Func<TEvent, TOperations, CancellationToken, Task> project)
    {
        _project = project;
    }
    
    public Type EventType => typeof(TEvent).UnwrapEventType();
}

internal class EventHandler<TOperations> : MethodCall
{
    public EventHandler(Type handlerType, string methodName) : base(handlerType, methodName)
    {
    }

    public EventHandler(Type handlerType, MethodInfo method) : base(handlerType, method)
    {
    }
    
    public Type EventType => Method.GetEventType(null).UnwrapEventType();
}