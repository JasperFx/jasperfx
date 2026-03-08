namespace JasperFx.Events;

/// <summary>
/// Represents a single tag on an event — a (TagType, Value) pair where TagType
/// is a strong-typed identifier type (e.g., typeof(StudentId)) and Value is the
/// actual strong-typed identifier instance (e.g., new StudentId("STU-001")).
/// The value is only unwrapped to its primitive form at the database boundary.
/// </summary>
public readonly record struct EventTag(Type TagType, object Value);
