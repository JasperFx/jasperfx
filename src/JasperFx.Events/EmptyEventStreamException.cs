namespace JasperFx.Events;

public class EmptyEventStreamException : Exception
{
    public static readonly string MessageTemplate =
        "A new event stream ('{0}') cannot be started without any events";

    public EmptyEventStreamException(string key): base(string.Format(MessageTemplate, key))
    {
    }

    public EmptyEventStreamException(Guid id): base(string.Format(MessageTemplate, id))
    {
    }
}
