namespace JasperFx.Core.Descriptions;

/// <summary>
/// Service to create a description of the EventStoreUsage in the application
/// </summary>
public interface IEventStoreCapability
{
    Task<EventStoreUsage?> TryCreateUsage(CancellationToken token);
}