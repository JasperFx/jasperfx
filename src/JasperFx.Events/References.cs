namespace JasperFx.Events;

/// <summary>
/// Stand in event used by projections to just denote a document or entity
/// that is referenced by an aggregate projection. Synthetic event.
/// </summary>
/// <param name="Entity"></param>
/// <typeparam name="T"></typeparam>
public record References<T>(T Entity) : ISyntheticEvent;

/// <summary>
/// Marks the data of a stand in event: one a projection added to a slice, which was never appended to
/// a stream and so has no correlation or causation of its own to contribute.
/// </summary>
internal interface ISyntheticEvent;