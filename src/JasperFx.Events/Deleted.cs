namespace JasperFx.Events;

/// <summary>
/// Stand in event used by projections to just denote a document or entity
/// that was deleted as part of an input to an aggregate projection. Synthetic event.
/// </summary>
/// <param name="Identity"></param>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
public record ProjectionDeleted<TDoc, TId>(TId Identity, string TenantId) : ICanWrapEvent, ProjectionDeleted<TDoc>, DeletedIdentity<TId>
{
    public IEvent ToEvent()
    {
        return Event.For(TenantId, this);
    }
}

public interface ProjectionDeleted;

/// <summary>
/// Just a marker interface to make Deleted<TDoc, TId> easier to use in downstream composite projections
/// </summary>
/// <typeparam name="TId"></typeparam>
public interface DeletedIdentity<TId>
{
    TId Identity { get; }
}

/// <summary>
/// Just a marker interface to make Deleted<TDoc, TId> easier to use in in downstream composite projections
/// </summary>
/// <typeparam name="TDoc"></typeparam>
public interface ProjectionDeleted<TDoc> : ProjectionDeleted;