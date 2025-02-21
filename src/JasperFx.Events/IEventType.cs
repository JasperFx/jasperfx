namespace JasperFx.Events;

public interface IEventType
{
    Type EventType { get; }
    string DotNetTypeName { get; set; }
    string EventTypeName { get; set; }
    string Alias { get; }
}
