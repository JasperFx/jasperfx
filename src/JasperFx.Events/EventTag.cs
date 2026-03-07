namespace JasperFx.Events;

/// <summary>
/// Represents a single tag on an event — a (TagType, Value) pair where TagType
/// is a strong-typed identifier (e.g., StudentId) and Value is the unwrapped primitive.
/// </summary>
public readonly record struct EventTag(Type TagType, object Value);
