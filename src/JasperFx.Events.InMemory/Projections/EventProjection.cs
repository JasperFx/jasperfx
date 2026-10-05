using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory.Projections;

/// <summary>
/// An event projection on the in-memory prototyping store (jasperfx#964): explicit code run for each
/// event, writing documents through the session.
/// </summary>
/// <remarks>
/// Named like Marten's and Fisher's so a projection class moves between stores with a <c>using</c>
/// change. Inline only: documents it stores land in the same commit as the events that produced them.
/// </remarks>
public abstract class EventProjection : JasperFxEventProjectionBase<IInMemoryDocumentSession, IInMemoryQuerySession>
{
    /// <inheritdoc />
    protected sealed override void storeEntity<T>(IInMemoryDocumentSession ops, T entity) => ops.Store(entity);
}
