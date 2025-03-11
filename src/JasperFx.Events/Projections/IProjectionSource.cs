using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

/// <summary>
/// Interface for sources of projections
/// Sources of projections are used to define the behavior how a projection is built for a given projection type
/// Optimized for async usage
/// </summary>
public interface IProjectionSource<TOperations, TQuerySession>: IReadOnlyProjectionData, ISubscriptionSource<TOperations, TQuerySession> 
    where TOperations : TQuerySession, IStorageOperations
{
    bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database, out IReplayExecutor executor);

    IInlineProjection<TOperations> BuildForInline();
}