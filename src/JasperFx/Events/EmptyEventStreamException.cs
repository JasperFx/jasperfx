namespace JasperFx.Events;

public class EmptyEventStreamException: Exception
{
    public EmptyEventStreamException(string key): base($"A new event stream ('{key}') cannot be started without any events")
    {
    }

    public EmptyEventStreamException(Guid id): base($"A new event stream ('{id}') cannot be started without any events")
    {
    }
}
