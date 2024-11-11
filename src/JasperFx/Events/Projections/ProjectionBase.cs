#nullable enable
using System.Diagnostics.CodeAnalysis;

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

public abstract class EventFilterable: IEventFilterable
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

public abstract class ProjectionBase : EventFilterable
{
    private readonly List<Type> _publishedTypes = new();

    /// <summary>
    ///     Descriptive name for this projection in the async daemon. The default is the type name of the projection
    /// </summary>
    [DisallowNull]
    public string? ProjectionName { get; set; }

    /// <summary>
    /// Specify that this projection is a non 1 version of the original projection definition to opt
    /// into Marten's parallel blue/green deployment of this projection.
    /// </summary>
    public uint ProjectionVersion { get; set; } = 1;

    /// <summary>
    ///     The projection lifecycle that governs when this projection is executed
    /// </summary>
    public ProjectionLifecycle Lifecycle { get; set; } = ProjectionLifecycle.Async;

    public virtual void AssembleAndAssertValidity()
    {
        // Nothing
    }

    /// <summary>
    ///     Just recording which document types are published by this projection
    /// </summary>
    /// <param name="publishedType"></param>
    protected void RegisterPublishedType(Type publishedType)
    {
        _publishedTypes.Add(publishedType);
    }

    public IEnumerable<Type> PublishedTypes()
    {
        return _publishedTypes;
    }
}
