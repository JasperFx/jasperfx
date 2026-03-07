using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Tags;

/// <summary>
/// Registration of a strong-typed identifier as an event tag type.
/// </summary>
public class TagTypeRegistration
{
    public TagTypeRegistration(Type tagType)
    {
        TagType = tagType;
        ValueTypeInfo = ValueTypeInfo.ForType(tagType);
        TableSuffix = tagType.Name.ToTableAlias();
    }

    public TagTypeRegistration(Type tagType, string tableSuffix)
    {
        TagType = tagType;
        ValueTypeInfo = ValueTypeInfo.ForType(tagType);
        TableSuffix = tableSuffix;
    }

    /// <summary>
    /// The strong-typed identifier type (e.g., typeof(StudentId))
    /// </summary>
    public Type TagType { get; }

    /// <summary>
    /// Resolved value type info for the tag type
    /// </summary>
    public ValueTypeInfo ValueTypeInfo { get; }

    /// <summary>
    /// Table suffix for the tag table (e.g., "student_id")
    /// </summary>
    public string TableSuffix { get; }

    /// <summary>
    /// The inner primitive type (e.g., typeof(string), typeof(Guid))
    /// </summary>
    public Type SimpleType => ValueTypeInfo.SimpleType;

    /// <summary>
    /// The associated aggregate type, if any. Inferred from document mapping
    /// or set explicitly via ForAggregate.
    /// </summary>
    public Type? AggregateType { get; set; }

    /// <summary>
    /// Associate this tag type with a specific aggregate type.
    /// </summary>
    public TagTypeRegistration ForAggregate<T>()
    {
        AggregateType = typeof(T);
        return this;
    }

    /// <summary>
    /// Associate this tag type with a specific aggregate type.
    /// </summary>
    public TagTypeRegistration ForAggregate(Type aggregateType)
    {
        AggregateType = aggregateType;
        return this;
    }

    /// <summary>
    /// Extract the inner primitive value from a strong-typed identifier instance.
    /// Uses a memoized compiled lambda for performance — no reflection at runtime.
    /// </summary>
    public object ExtractValue(object tagInstance)
    {
        return TagValueExtractor.ExtractValue(TagType, tagInstance);
    }
}
