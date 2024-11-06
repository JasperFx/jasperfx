using System.Runtime.Serialization;

namespace JasperFx.Events;

public class EventStreamUnexpectedMaxEventIdException: ConcurrencyException
{
    public EventStreamUnexpectedMaxEventIdException(object id, Type aggregateType, long expected, long actual): base(
        $"Unexpected starting version number for event stream '{id}', expected {expected} but was {actual}")
    {
        Id = id;
    }

    protected EventStreamUnexpectedMaxEventIdException(SerializationInfo info, StreamingContext context): base(info,
        context)
    {
    }

    public EventStreamUnexpectedMaxEventIdException(string message) : base(message)
    {
    }

}
