using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.Subscriptions;

namespace JasperFx.Events.Projections;

/// <summary>
/// Interface for sources of projections
/// Sources of projections are used to define the behavior how a projection is built for a given projection type
/// Optimized for async usage
/// </summary>
public interface IProjectionSource<TOperations, TQuerySession>: ISubscriptionSource<TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
{
    bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)]out IReplayExecutor? executor);

    IInlineProjection<TOperations> BuildForInline();

    IEnumerable<Type> PublishedTypes();
}