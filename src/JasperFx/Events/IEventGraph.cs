#nullable enable
namespace JasperFx.Events;

public interface IEventGraph
{
    IEvent BuildEvent(object eventData);
    EventAppendMode AppendMode { get; set; }

    /// <summary>
    /// TimeProvider used for event timestamping metadata. Replace for controlling the timestamps
    /// in testing
    /// </summary>
    TimeProvider TimeProvider { get; set; }

    string AggregateAliasFor(Type aggregateType);
}
