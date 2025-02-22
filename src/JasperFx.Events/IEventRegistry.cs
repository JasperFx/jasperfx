using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

public enum EventAppendMode
{
    /// <summary>
    /// Default behavior that ensures that all inline projections will have full access to all event
    /// metadata including intended event sequences, versions, and timestamps
    /// </summary>
    Rich,

    /// <summary>
    /// Stripped down, more performant mode of appending events that will omit some event metadata within
    /// inline projections
    /// </summary>
    Quick
}

public interface IEventStorageBuilder<T>
{
    T QuickAppendEventWithVersion(StreamAction action, IEvent @event);
    T UpdateStreamVersion(StreamAction action);
    T QuickAppendEvents(StreamAction action);
}

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
}

public class EventRegistry : IEventRegistry
{
    private ImHashMap<Type, IEventType> _eventTypes = ImHashMap<Type, IEventType>.Empty;
    
    public IEvent BuildEvent(object eventData)
    {
        if (eventData == null)
        {
            throw new ArgumentNullException(nameof(eventData));
        }

        var info = EventMappingFor(eventData.GetType());
        return info.Wrap(eventData);
    }

    public StreamIdentity StreamIdentity { get; set; } = StreamIdentity.AsGuid;

    public IEventType EventMappingFor(Type eventType)
    {
        if (_eventTypes.TryFind(eventType, out var info))
        {
            return info;
        }

        info = typeof(EventTypeData<>).CloseAndBuildAs<IEventType>(eventType);
        _eventTypes = _eventTypes.AddOrUpdate(eventType, info);

        return info;
    }

    public EventAppendMode AppendMode { get; set; } = EventAppendMode.Rich;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    public Type AggregateTypeFor(string aggregateTypeName)
    {
        throw new NotImplementedException();
    }

    public string AggregateAliasFor(Type aggregateType)
    {
        throw new NotImplementedException();
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

