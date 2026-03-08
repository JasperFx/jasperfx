namespace JasperFx.Events.Aggregation;

/// <summary>
/// Marker interface to explicitly declare the identity type of an aggregate.
/// This is especially useful for aggregates that use strong typed identifiers
/// (e.g., Vogen value objects wrapping Guid/string) to enable automatic
/// identity inference in command handlers.
/// </summary>
public interface IdentifiedBy<T>;
