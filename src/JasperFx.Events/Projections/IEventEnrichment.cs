namespace JasperFx.Events.Projections;

/// <summary>
/// Marker interface for projections that support event enrichment before processing.
/// Implement this to batch-load reference data and enrich events before they are applied.
/// </summary>
public interface IEventEnrichment<in TQuerySession>
{
    /// <summary>
    /// Enrich events with additional data before they are processed.
    /// Called once per tenant batch.
    /// </summary>
    Task EnrichEventsAsync(TQuerySession querySession, IReadOnlyList<IEvent> events,
        CancellationToken cancellation);
}
