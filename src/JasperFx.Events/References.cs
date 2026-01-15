namespace JasperFx.Events;

/// <summary>
/// Stand in event used by projections to just denote a document or entity
/// that is referenced by an aggregate projection. Synthetic event.
/// </summary>
/// <param name="Entity"></param>
/// <typeparam name="T"></typeparam>
public record References<T>(T Entity);