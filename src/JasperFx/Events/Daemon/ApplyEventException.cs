namespace JasperFx.Events.Daemon;

public class ApplyEventException: Exception
{
    public ApplyEventException(IEvent @event, Exception innerException): base(
        $"Failure to apply event #{@event.Sequence} Id({@event.Id})", innerException)
    {
        Event = @event;
    }

    public IEvent Event { get; }
}
