namespace JasperFx.Events.Aggregation;

/// <summary>
/// Interface for source-generated evolvers that wrap a user's async Evolve/EvolveAsync
/// method on a self-aggregating type. Used when the aggregate defines its own
/// Evolve(IEvent) or EvolveAsync(IEvent, IQuerySession) method.
/// </summary>
public interface IGeneratedAsyncEvolver<TDoc, TId> where TDoc : notnull where TId : notnull
{
    ValueTask<TDoc?> EvolveAsync(TDoc? snapshot, TId id, IEvent e, object session, CancellationToken ct);
    Type[] EventTypes { get; }
}
