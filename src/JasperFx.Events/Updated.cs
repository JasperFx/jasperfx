namespace JasperFx.Events;

/// <summary>
/// Stand in event used by projections to just denote a document or entity
/// that was updated as part of an input to an aggregate projection. Synthetic event.
/// </summary>
/// <param name="Document"></param>
/// <typeparam name="T"></typeparam>
public record Updated<T>(string TenantId, T Entity);