using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

/// <summary>
/// Interface for source-generated evolvers that handle all-sync conventional methods
/// with ShouldDelete. Used for self-aggregating types.
/// </summary>
public interface IGeneratedSyncDetermineAction<TDoc, TId> where TDoc : notnull where TId : notnull
{
    (TDoc?, ActionType) DetermineAction(TDoc? snapshot, TId id, IReadOnlyList<IEvent> events);
    Type[] EventTypes { get; }
}
