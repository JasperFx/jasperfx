namespace JasperFx.Events.Projections;

public interface IEventLoader
{
    Task<EventPage> LoadAsync(EventRequest request, CancellationToken token);
}
