using JasperFx.Core;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

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