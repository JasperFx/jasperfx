using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.NewStuff;

/// <summary>
/// Interface for sources of projections
/// Sources of projections are used to define the behavior how a projection is built for a given projection type
/// Optimized for async usage
/// </summary>
public interface IProjectionSource<TOperations, TStore, TDatabase>: IReadOnlyProjectionData, ISubscriptionSource<TStore, TDatabase>
{
    bool TryBuildReplayExecutor(TStore store, TDatabase database, out IReplayExecutor executor);

    IInlineProjection<TOperations> BuildForInline();
    
}