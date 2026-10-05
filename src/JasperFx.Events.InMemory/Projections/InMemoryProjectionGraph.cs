using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory.Projections;

/// <summary>
/// A projection that runs against the in-memory prototyping store (jasperfx#964).
/// </summary>
public interface IInMemoryProjection : IJasperFxProjection<IInMemoryDocumentSession>;

/// <summary>
/// The projections registered on the in-memory prototyping store (jasperfx#964), and the source of the
/// aggregators that live aggregation (<c>AggregateStreamAsync</c>, <c>FetchForWriting</c>,
/// <c>FetchLatest</c>) folds with.
/// </summary>
/// <remarks>
/// Live and Inline only: the async daemon is out of scope for a prototyping store.
/// </remarks>
public class InMemoryProjectionGraph
    : ProjectionGraph<IInMemoryProjection, IInMemoryDocumentSession, IInMemoryQuerySession>
{
    private readonly InMemoryEventRegistry _events;

    internal InMemoryProjectionGraph(InMemoryEventRegistry events) : base(events, "inmemory")
    {
        _events = events;
    }

    /// <inheritdoc />
    protected override void onAddProjection(object projection)
    {
        if (projection is ProjectionBase projectionBase)
        {
            foreach (var eventType in projectionBase.IncludedEventTypes)
            {
                _events.AddEventType(eventType);
            }
        }
    }
}
