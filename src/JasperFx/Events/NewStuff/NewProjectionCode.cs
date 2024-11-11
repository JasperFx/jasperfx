using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.NewStuff;

/// <summary>
///     Interface for projections applied "Inline" as part of saving a transaction
/// </summary>
public interface IInlineProjection<T>
{
    /// <summary>
    ///     Apply inline projections during asynchronous operations
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="streams"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyAsync(T operations, IReadOnlyList<StreamAction> streams,
        CancellationToken cancellation);
}

/// <summary>
/// Main entry point for non-aggregation projections
/// </summary>
public interface IProjection<T>
{
    /// <summary>
    ///     Apply inline projections during asynchronous operations
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="events"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyAsync(T operations, IReadOnlyList<IEvent> events, CancellationToken cancellation);
}


// Leave public for codegen!
public interface IAggregator<T, TQuerySession>
{
    ValueTask<T> BuildAsync(
        IReadOnlyList<IEvent> events,
        TQuerySession session,
        T? snapshot,
        CancellationToken cancellation);
}

public interface IAggregatorSource<TQuerySession>
{
    Type AggregateType { get; }
    IAggregator<T, TQuerySession> Build<T>();
}

/// <summary>
/// Interface for sources of projections
/// Sources of projections are used to define the behavior how a projection is built for a given projection type
/// Optimized for async usage
/// </summary>
public interface IProjectionSource: IReadOnlyProjectionData
{
    AsyncOptions Options { get; }

    /// <summary>
    ///     This is *only* a hint to Marten about what projected document types
    ///     are published by this projection to aid the "generate ahead" model
    /// </summary>
    /// <returns></returns>
    IEnumerable<Type> PublishedTypes();

    // TODO -- might need to make this be async
    IReadOnlyList<IAsyncShard> AsyncProjectionShards();

    /// <summary>
    /// Specify that this projection is a non 1 version of the original projection definition to opt
    /// into Marten's parallel blue/green deployment of this projection.
    /// </summary>
    uint ProjectionVersion { get; }

    // TODO -- might need to make this be async
    bool TryBuildReplayExecutor(string databaseName, out IReplayExecutor executor);
}

public interface ISubscriptionSource
{
    public AsyncOptions Options { get; }
    // TODO -- might need to make this be async
    IReadOnlyList<IAsyncShard> AsyncProjectionShards();

    public string SubscriptionName { get; }
    public uint SubscriptionVersion { get; }
}

// Assuming that DocumentStore et al will be embedded into this
public interface IAsyncShard
{
    ShardRole Role { get; }
    ISubscriptionExecution BuildExecution(ILogger logger);
    ShardName Name { get; }
}




/*
public interface IAggregationRuntime<TOperations, TDoc, TId>: IAggregationRuntime where TDoc : notnull where TId : notnull
{
    IEventSlicer<TDoc, TId> Slicer { get; }


    ValueTask ApplyChangesAsync(TOperations session,
        EventSlice<TDoc, TId> slice, CancellationToken cancellation,
        ProjectionLifecycle lifecycle = ProjectionLifecycle.Inline);

    bool IsNew(EventSlice<TDoc, TId> slice);

    IAggregateCache<TId, TDoc> CacheFor(Tenant tenant);

    TId IdentityFromEvent(IEvent e);
}
*/