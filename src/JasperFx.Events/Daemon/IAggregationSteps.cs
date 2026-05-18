namespace JasperFx.Events.Daemon;

/// <summary>
///     Fluent surface that hangs off aggregation projection base classes to register
///     event types that affect lifecycle outside of the conventional Apply/Create/ShouldDelete
///     methods on a partial projection class.
/// </summary>
/// <typeparam name="T"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
public interface IAggregationSteps<T, out TQuerySession>
{
    /// <summary>
    ///     Delete the aggregate document when event of type TEvent is encountered.
    /// </summary>
    IAggregationSteps<T, TQuerySession> DeleteEvent<TEvent>() where TEvent : class;

    /// <summary>
    ///     Register a source event that is transformed within this aggregation. This is important for
    ///     asynchronous projections to enable the projection to subscribe to the source event type
    ///     even though there are no direct handlers for the source event type.
    /// </summary>
    IAggregationSteps<T, TQuerySession> TransformsEvent<TEvent>() where TEvent : class;
}
