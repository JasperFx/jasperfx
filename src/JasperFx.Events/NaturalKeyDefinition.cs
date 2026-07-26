using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

/// <summary>
/// Defines the mapping from a specific event type to a natural key value extraction.
/// </summary>
public class NaturalKeyEventMapping
{
    public NaturalKeyEventMapping(Type eventType, Func<IEvent, object?> extractor)
    {
        EventType = eventType;
        Extractor = extractor;
    }

    public Type EventType { get; }

    /// <summary>
    /// Derives the natural key value carried by a single event. Receives the whole <see cref="IEvent" />
    /// rather than just <see cref="IEvent.Data" /> (jasperfx#569) so that an <c>IEvent&lt;T&gt;</c> handler
    /// — a first class signature everywhere else in aggregation discovery — is directly bindable, and so
    /// that a key derived from event metadata (stream key, timestamp, headers) is expressible at all.
    /// </summary>
    public Func<IEvent, object?> Extractor { get; }
}

/// <summary>
/// A <c>[NaturalKeySource]</c> method that discovery could not turn into a usable extractor, along with
/// the reason. Surfaced instead of being swallowed so that projection validation can fail loudly at
/// configuration time naming the method — see jasperfx#569.
/// </summary>
public class NaturalKeySourceProblem
{
    public NaturalKeySourceProblem(MethodInfo method, Type eventType, string reason)
    {
        Method = method;
        EventType = eventType;
        Reason = reason;
    }

    public MethodInfo Method { get; }
    public Type EventType { get; }
    public string Reason { get; }

    public override string ToString()
        => $"{Method.DeclaringType?.FullNameInCode()}.{Method.Name}() for event {EventType.FullNameInCode()}: {Reason}";
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
    private readonly List<NaturalKeySourceProblem> _problems = new();

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
    /// <c>[NaturalKeySource]</c> methods that discovery could not bind, and why. Empty when every
    /// annotated method produced a mapping. Projection validation turns any leftovers into an
    /// <c>InvalidProjectionException</c> at configuration time rather than a natural key lookup table
    /// that is silently never written. See jasperfx#569.
    /// </summary>
    public IReadOnlyList<NaturalKeySourceProblem> DiscoveryProblems => _problems;

    /// <summary>
    /// Is there already a key extraction registered for this event type?
    /// </summary>
    public bool HasMappingFor(Type eventType) => EventMappings.Any(x => x.EventType == eventType);

    /// <summary>
    /// Register (or replace) the key extraction for an event type. Replacing is what lets an explicit
    /// <see cref="NaturalKeyBuilder{TDoc}" /> registration override — and clear the recorded problem of —
    /// an attribute-discovered method that discovery could not bind.
    /// </summary>
    public void AddOrReplaceMapping(Type eventType, Func<IEvent, object?> extractor)
    {
        EventMappings.RemoveAll(x => x.EventType == eventType);
        EventMappings.Add(new NaturalKeyEventMapping(eventType, extractor));
        _problems.RemoveAll(x => x.EventType == eventType);
    }

    /// <summary>
    /// Record a <c>[NaturalKeySource]</c> method that could not be bound to an extractor.
    /// </summary>
    public void RecordProblem(MethodInfo method, Type eventType, string reason)
    {
        if (HasMappingFor(eventType)) return;

        _problems.Add(new NaturalKeySourceProblem(method, eventType, reason));
    }

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
