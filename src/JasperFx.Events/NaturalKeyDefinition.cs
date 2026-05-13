using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

/// <summary>
/// Defines the mapping from a specific event type to a natural key value extraction.
/// </summary>
public class NaturalKeyEventMapping
{
    public NaturalKeyEventMapping(Type eventType, Func<object, object?> extractor)
    {
        EventType = eventType;
        Extractor = extractor;
    }

    public Type EventType { get; }
    public Func<object, object?> Extractor { get; }
}

/// <summary>
/// Metadata describing a natural key on an aggregate type. A natural key provides an
/// alternative lookup for event streams using a domain-meaningful strong-typed identifier.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: scans event types via reflection (PublicProperties + ValueTypeInfo) to extract natural-key values. Event and key types preserved by the registered projection boundary on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "Class-level: reflective Type results assigned to DAM-annotated targets during natural-key discovery. Source types preserved at registration.")]
public class NaturalKeyDefinition
{
    public NaturalKeyDefinition(Type aggregateType, MemberInfo member)
    {
        AggregateType = aggregateType;
        Member = member;

        var memberType = member.GetMemberType()!;
        OuterType = memberType;

        // Determine if this is a strong-typed id (value type wrapper) or a primitive
        if (IsPrimitiveKeyType(memberType))
        {
            InnerType = memberType;
        }
        else
        {
            try
            {
                var valueTypeInfo = ValueTypeInfo.ForType(memberType);
                ValueTypeInfo = valueTypeInfo;
                InnerType = valueTypeInfo.SimpleType;
            }
            catch (Exception)
            {
                // Not a valid value type wrapper, treat as primitive
                InnerType = memberType;
            }
        }
    }

    public Type AggregateType { get; }
    public MemberInfo Member { get; }

    /// <summary>
    /// The outer type of the natural key (may be a strong-typed id wrapper).
    /// </summary>
    public Type OuterType { get; }

    /// <summary>
    /// The inner/primitive type of the natural key (int, long, or string).
    /// </summary>
    public Type InnerType { get; }

    /// <summary>
    /// Value type info for wrapping/unwrapping strong-typed identifiers. Null if the key is a primitive.
    /// </summary>
    public ValueTypeInfo? ValueTypeInfo { get; }

    /// <summary>
    /// Event-to-key mappings registered via SetBy or [NaturalKeySource].
    /// </summary>
    public List<NaturalKeyEventMapping> EventMappings { get; } = new();

    /// <summary>
    /// Unwrap a natural key value to its inner primitive representation.
    /// </summary>
    public object? Unwrap(object? value)
    {
        if (value == null) return null;
        if (ValueTypeInfo == null) return value;

        // Use reflection to call the generic UnWrapper method
        return ValueTypeInfo.ValueProperty.GetValue(value);
    }

    /// <summary>
    /// Validates that the inner type is a supported natural key type.
    /// </summary>
    public bool IsValid()
    {
        return InnerType == typeof(int) || InnerType == typeof(long) || InnerType == typeof(string);
    }

    private static bool IsPrimitiveKeyType(Type type)
    {
        return type == typeof(int) || type == typeof(long) || type == typeof(string);
    }
}
