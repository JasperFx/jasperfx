using System.Runtime.Serialization;

namespace JasperFx.Events;

public class EventStreamUnexpectedMaxEventIdException: ConcurrencyException
{
    public EventStreamUnexpectedMaxEventIdException(object id, Type? aggregateType, long expected, long actual): base(
        $"Unexpected starting version number for event stream '{id}', expected {expected} but was {actual}",
        aggregateType, id)
    {
        Id = id;
        AggregateType = aggregateType;
    }

    public EventStreamUnexpectedMaxEventIdException(string? message) : base(message)
    {
    }

    public Type? AggregateType { get; }
}
