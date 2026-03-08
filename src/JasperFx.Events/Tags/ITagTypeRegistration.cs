namespace JasperFx.Events.Tags;

/// <summary>
/// Non-generic interface for tag type registrations, used throughout the codebase
/// so consumers don't need to know the generic type parameters.
/// </summary>
public interface ITagTypeRegistration
{
    /// <summary>
    /// The strong-typed identifier type (e.g., typeof(StudentId))
    /// </summary>
    Type TagType { get; }

    /// <summary>
    /// The inner primitive type (e.g., typeof(string), typeof(Guid))
    /// </summary>
    Type SimpleType { get; }

    /// <summary>
    /// Table suffix for the tag table (e.g., "student_id")
    /// </summary>
    string TableSuffix { get; }

    /// <summary>
    /// The associated aggregate type, if any. Inferred from document mapping
    /// or set explicitly via ForAggregate.
    /// </summary>
    Type? AggregateType { get; set; }

    /// <summary>
    /// Extract the inner primitive value from a strong-typed identifier instance.
    /// </summary>
    object ExtractValue(object tagInstance);

    /// <summary>
    /// Associate this tag type with a specific aggregate type.
    /// </summary>
    ITagTypeRegistration ForAggregate<T>();

    /// <summary>
    /// Associate this tag type with a specific aggregate type.
    /// </summary>
    ITagTypeRegistration ForAggregate(Type aggregateType);
}
