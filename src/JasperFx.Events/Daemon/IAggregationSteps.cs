namespace JasperFx.Events.Daemon;

/// <summary>
///     Fluent interface option for expressing aggregation projections
/// </summary>
/// <typeparam name="T"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
public interface IAggregationSteps<T, out TQuerySession>
{
    /// <summary>
    ///     Create a new instance of the aggregate T when the event TEvent is encountered -- if the aggregate does
    ///     not already exist
    /// </summary>
    /// <param name="creator"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> CreateEvent<TEvent>(Func<TEvent, T> creator) where TEvent : class;

    /// <summary>
    ///     Create a new instance of the aggregate T when the event TEvent is encountered -- if the aggregate does
    ///     not already exist
    /// </summary>
    /// <param name="creator"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> CreateEvent<TEvent>(Func<TEvent, TQuerySession, Task<T>> creator) where TEvent : class;

    /// <summary>
    ///     Delete the aggregate document when event of type TEvent is encountered
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> DeleteEvent<TEvent>() where TEvent : class;

    /// <summary>
    ///     Conditionally delete the aggregate document when event of type TEvent is encountered based on the supplied
    ///     shouldDelete test
    /// </summary>
    /// <param name="shouldDelete"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> DeleteEvent<TEvent>(Func<TEvent, bool> shouldDelete) where TEvent : class;

    /// <summary>
    ///     Conditionally delete the aggregate document when event of type TEvent is encountered based on the supplied
    ///     shouldDelete test
    /// </summary>
    /// <param name="shouldDelete"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> DeleteEvent<TEvent>(Func<T, TEvent, bool> shouldDelete) where TEvent : class;

    /// <summary>
    ///     Conditionally delete the aggregate document when event of type TEvent is encountered based on the supplied
    ///     shouldDelete test
    /// </summary>
    /// <param name="shouldDelete"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> DeleteEventAsync<TEvent>(
        Func<TQuerySession, T, TEvent, Task<bool>> shouldDelete) where TEvent : class;

    /// <summary>
    ///     Apply changes to the existing aggregate based on the event type TEvent
    /// </summary>
    /// <param name="handler"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> ProjectEvent<TEvent>(Action<T> handler)
        where TEvent : class;

    /// <summary>
    ///     Apply changes to the existing aggregate based on the event type TEvent
    /// </summary>
    /// <param name="handler"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> ProjectEvent<TEvent>(Action<T, TEvent> handler)
        where TEvent : class;

    /// <summary>
    ///     Apply changes to the existing aggregate based on the event type TEvent and return
    ///     a new aggregate. This is appropriate for immutable aggregate documents
    /// </summary>
    /// <param name="handler"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> ProjectEvent<TEvent>(Func<T, TEvent, T> handler)
        where TEvent : class;

    /// <summary>
    ///     Apply changes to the existing aggregate based on the event type TEvent and return
    ///     a new aggregate. This is appropriate for immutable aggregate documents
    /// </summary>
    /// <param name="handler"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> ProjectEvent<TEvent>(Func<T, T> handler)
        where TEvent : class;

    /// <summary>
    ///     Apply changes to the existing aggregate based on the event type TEvent
    /// </summary>
    /// <param name="handler"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> ProjectEvent<TEvent>(Action<TQuerySession, T, TEvent> handler)
        where TEvent : class;

    /// <summary>
    ///     Apply changes to the existing aggregate based on the event type TEvent.
    /// </summary>
    /// <param name="handler"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> ProjectEventAsync<TEvent>(Func<TQuerySession, T, TEvent, Task> handler) where TEvent : class;

    /// <summary>
    ///     Apply changes to the existing aggregate based on the event type TEvent and return
    ///     a new aggregate. This is appropriate for immutable aggregate documents
    /// </summary>
    /// <param name="handler"></param>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> ProjectEventAsync<TEvent>(Func<TQuerySession, T, TEvent, Task<T>> handler) where TEvent : class;


    /// <summary>
    ///     Register a source event that is transformed within this aggregation. This is important for
    ///     asynchronous projections to enable the projection to subscribe to the source event type
    ///     event though there are no direct handlers for the source event type
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    /// <returns></returns>
    IAggregationSteps<T, TQuerySession> TransformsEvent<TEvent>() where TEvent : class;
}