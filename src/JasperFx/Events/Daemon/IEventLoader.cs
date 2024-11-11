using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public interface IEventLoader
{
    Task<EventPage> LoadAsync(EventRequest request, CancellationToken token);
}
