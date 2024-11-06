using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

public interface IDaemonRuntime
{
    Task RecordDeadLetterEventAsync(DeadLetterEvent @event);
    ILogger Logger { get; }
    long HighWaterMark();
}
