using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.NewStuff;

/// <summary>
///     Interface for projections applied "Inline" as part of saving a transaction
/// </summary>
public interface IInlineProjection<TOperations>
{
    /// <summary>
    ///     Apply inline projections during asynchronous operations
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="streams"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams,
        CancellationToken cancellation);
}

/// <summary>
/// Main entry point for non-aggregation projections
/// </summary>
public interface IProjection<TOperations>
{
    /// <summary>
    ///     Apply inline projections during asynchronous operations
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="events"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation);
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
public interface IProjectionSource<TStore, TDatabase>: IReadOnlyProjectionData, ISubscriptionSource<TStore, TDatabase>
{
    // TODO -- might need to make this be async
    bool TryBuildReplayExecutor(string databaseName, out IReplayExecutor executor);
}

public interface ISubscriptionSource<TStore, TDatabase>
{
    public AsyncOptions Options { get; }
    // TODO -- might need to make this be async
    IReadOnlyList<IAsyncShard<TStore, TDatabase>> AsyncProjectionShards();

    public string Name { get; }
    public uint Version { get; }
}

// Assuming that DocumentStore et al will be embedded into this
public interface IAsyncShard<TStore, TDatabase>
{
    ShardRole Role { get; }
    ISubscriptionExecution BuildExecution(TStore store, TDatabase database, ILogger logger);
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