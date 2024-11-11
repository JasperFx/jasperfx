using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public interface IDaemonRuntime
{
    Task RecordDeadLetterEventAsync(DeadLetterEvent @event);
    ILogger Logger { get; }
    long HighWaterMark();
}
