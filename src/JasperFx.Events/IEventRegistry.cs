using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Aggregation;

namespace JasperFx.Events;

public interface IEventRegistry
{
    IEvent BuildEvent(object eventData);
    EventAppendMode AppendMode { get; set; }

    /// <summary>
    /// TimeProvider used for event timestamping metadata. Replace for controlling the timestamps
    /// in testing
    /// </summary>
    TimeProvider TimeProvider { get; set; }

    /// <summary>
    ///     Configure whether event streams are identified with Guid or strings
    /// </summary>
    StreamIdentity StreamIdentity { get; set; }

    Type AggregateTypeFor(string aggregateTypeName);
    string AggregateAliasFor(Type aggregateType);

    IEventType EventMappingFor(Type eventType);

    /// <summary>
    ///     Register an event type. This isn't strictly necessary for normal usage,
    ///     but can help with asynchronous projections where the daemon hasn't yet encountered
    ///     the event type
    /// </summary>
    /// <param name="eventType"></param>
    void AddEventType(Type eventType);
}

/// <summary>
/// Implemented by event registries that can automatically derive aggregators
/// </summary>
public interface IAggregationSourceFactory<TQuerySession>
{
    /// <summary>
    /// Try to create an aggregate source for the type TDoc
    /// </summary>
    /// <typeparam name="TDoc">The aggregate type</typeparam>
    /// <returns></returns>
    IAggregatorSource<TQuerySession>? Build<TDoc>();
}

public class EventRegistry : IEventRegistry
{
    private ImHashMap<Type, IEventType> _eventTypes = ImHashMap<Type, IEventType>.Empty;

    private StreamIdentity _streamIdentity = StreamIdentity.AsGuid;

    public virtual StreamIdentity StreamIdentity
    {
        get => _streamIdentity;
        set => _streamIdentity = value;
    }

    private EventAppendMode _appendMode = EventAppendMode.Rich;

    public virtual EventAppendMode AppendMode
    {
        get => _appendMode;
        set => _appendMode = value;
    }

    [IgnoreDescription]
    public virtual TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public virtual IEvent BuildEvent(object eventData)
    {
        if (eventData == null)
        {
            throw new ArgumentNullException(nameof(eventData));
        }

        var info = EventMappingFor(eventData.GetType());
        return info.Wrap(eventData);
    }

    public virtual IEventType EventMappingFor(Type eventType)
    {
        if (_eventTypes.TryFind(eventType, out var info))
        {
            return info;
        }

        info = typeof(EventTypeData<>).CloseAndBuildAs<IEventType>(eventType);
        _eventTypes = _eventTypes.AddOrUpdate(eventType, info);

        return info;
    }

    public virtual void AddEventType(Type eventType)
    {
        EventMappingFor(eventType);
    }

    public virtual Type AggregateTypeFor(string aggregateTypeName)
    {
        throw new NotSupportedException("Override in a derived class to support aggregate type resolution.");
    }

    public virtual string AggregateAliasFor(Type aggregateType)
    {
        throw new NotSupportedException("Override in a derived class to support aggregate alias resolution.");
    }
}

public abstract class EventTypeData
{
    protected EventTypeData(Type eventType)
    {
        EventType = eventType;
        EventTypeName = eventType.GetEventTypeName();
        DotNetTypeName = $"{eventType.FullName}, {eventType.Assembly.GetName().Name}";
    }

    public Type EventType { get; }

    public string DotNetTypeName { get; set; }
    public string EventTypeName { get; set; }

    public string Alias => EventTypeName;
}

public class EventTypeData<T> : EventTypeData, IEventType where T : notnull
{
    public EventTypeData() : base(typeof(T))
    {
    }

    public IEvent Wrap(object data)
    {
        return new Event<T>((T)data) { EventTypeName = EventTypeName, DotNetTypeName = DotNetTypeName };
    }
}
