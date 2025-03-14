namespace JasperFx.Events.Projections;

public interface IEventFilterable
{
    /// <summary>
    ///     Short hand syntax to tell Marten that this projection takes in the event type T
    ///     This is not mandatory, but can be used to optimize the asynchronous projections
    ///     to create an "allow list" in the IncludedEventTypes collection
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void IncludeType<T>();

    /// <summary>
    ///     Short hand syntax to tell Marten that this projection takes in the event type T
    ///     This is not mandatory, but can be used to optimize the asynchronous projections
    ///     to create an "allow list" in the IncludedEventTypes collection
    /// </summary>
    void IncludeType(Type type);

    /// <summary>
    ///     Limit the events processed by this projection to only streams
    ///     marked with the given streamType.
    ///     ONLY APPLIED TO ASYNCHRONOUS PROJECTIONS OR SUBSCRIPTIONS
    /// </summary>
    /// <param name="streamType"></param>
    public void FilterIncomingEventsOnStreamType(Type streamType);

    /// <summary>
    /// Should archived events be considered for this filtered set? Default is false.
    /// </summary>
    public bool IncludeArchivedEvents { get; set; }
}