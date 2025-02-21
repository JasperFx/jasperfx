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

public interface IEventRegistry
{
    IEvent BuildEvent(object eventData);
    EventAppendMode AppendMode { get; set; }

    /// <summary>
    /// TimeProvider used for event timestamping metadata. Replace for controlling the timestamps
    /// in testing
    /// </summary>
    TimeProvider TimeProvider { get; set; }

    Type AggregateTypeFor(string aggregateTypeName);
    string AggregateAliasFor(Type aggregateType);
    
    
}