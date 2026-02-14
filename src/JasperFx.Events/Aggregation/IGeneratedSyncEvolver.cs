namespace JasperFx.Events.Aggregation;

/// <summary>
/// Interface for source-generated evolvers that handle all-sync conventional methods
/// without ShouldDelete. Used for self-aggregating types.
/// </summary>
public interface IGeneratedSyncEvolver<TDoc, TId> where TDoc : notnull where TId : notnull
{
    TDoc? Evolve(TDoc? snapshot, TId id, IEvent e);
    Type[] EventTypes { get; }
}
