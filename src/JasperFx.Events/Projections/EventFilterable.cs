using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events.Projections;

/// <summary>
/// Base class for any projection or subscription type that needs to filter based on
/// event type or stream type
/// </summary>
public class EventFilterable: IEventFilterable
{
    /// <summary>
    ///     Optimize this projection within the Async Daemon by
    ///     limiting the event types processed through this projection
    ///     to include type "T". This is inclusive.
    ///     If this list is empty, the async daemon will fetch every possible
    ///     type of event at runtime
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public List<Type> IncludedEventTypes { get; } = new();

    /// <summary>
    ///     Limit the events processed by this projection to only streams
    ///     marked with this stream type
    /// </summary>
    [DisallowNull]
    public Type? StreamType { get; set; }

    /// <summary>
    ///     Short hand syntax to tell Marten that this projection takes in the event type T
    ///     This is not mandatory, but can be used to optimize the asynchronous projections
    ///     to create an "allow list" in the IncludedEventTypes collection
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void IncludeType<T>()
    {
        IncludedEventTypes.Add(typeof(T));
    }

    /// <summary>
    ///     Short hand syntax to tell Marten that this projection takes in the event type T
    ///     This is not mandatory, but can be used to optimize the asynchronous projections
    ///     to create an "allow list" in the IncludedEventTypes collection
    /// </summary>
    public void IncludeType(Type type)
    {
        IncludedEventTypes.Add(type);
    }

    /// <summary>
    ///     Limit the events processed by this projection to only streams
    ///     marked with the given streamType.
    ///     ONLY APPLIED TO ASYNCHRONOUS PROJECTIONS OR SUBSCRIPTIONS
    /// </summary>
    /// <param name="streamType"></param>
    public void FilterIncomingEventsOnStreamType(Type streamType)
    {
        StreamType = streamType;
    }

    /// <summary>
    /// Should archived events be considered for this filtered set? Default is false.
    /// </summary>
    public bool IncludeArchivedEvents { get; set; }


}