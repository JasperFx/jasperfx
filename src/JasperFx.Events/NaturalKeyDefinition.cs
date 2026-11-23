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
    /// <summary>
    /// A mapping whose null result retires the stream's key: an explicit source.
    /// </summary>
    public NaturalKeyEventMapping(Type eventType, Func<IEvent, object?> extractor) : this(eventType, extractor, true)
    {
    }

    /// <param name="retiresKeyOnNull">See <see cref="RetiresKeyOnNull" />.</param>
    public NaturalKeyEventMapping(Type eventType, Func<IEvent, object?> extractor, bool retiresKeyOnNull)
    {
        EventType = eventType;
        Extractor = extractor;
        RetiresKeyOnNull = retiresKeyOnNull;
    }

    public Type EventType { get; }

    /// <summary>
    /// jasperfx#1287 (marten#6153): what a null from <see cref="Extractor" /> means. True for an explicit source,
    /// where null is a deliberate "this event removes the key": a <c>[NaturalKeySource]</c> static method that
    /// returns the key type, and a <c>NaturalKeyFor(x => x.SetBy...)</c> registration. False for a source discovery
    /// inferred, by reading a key-typed property off the event or by running an aggregation method against a blank
    /// aggregate. There a null only means the event did not carry a key, as in
    /// <c>Apply(CodeChanged e) { if (e.NewCode != null) Code = e.NewCode; }</c>, so the stream keeps its key.
    /// A store checks this before it retires a stream's key.
    /// </summary>
    public bool RetiresKeyOnNull { get; }

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
    public NaturalKeySourceProblem(MethodInfo method, Type eventType, string reason) : this(method, eventType, reason, false)
    {
    }

    /// <param name="hasOwnFix">See <see cref="HasOwnFix" />.</param>
    public NaturalKeySourceProblem(MethodInfo method, Type eventType, string reason, bool hasOwnFix)
    {
        Method = method;
        EventType = eventType;
        Reason = reason;
        HasOwnFix = hasOwnFix;
    }

    public MethodInfo Method { get; }
    public Type EventType { get; }
    public string Reason { get; }

    /// <summary>
    /// jasperfx#1287: true when <see cref="Reason" /> already says what to do, so the refusal does not need the
    /// general advice about deriving the key from the event alone, which would be wrong for this problem.
    /// </summary>
    public bool HasOwnFix { get; }

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

    public NaturalKeyDefinition(Type aggregateType, MemberInfo member) : this(aggregateType, member, null)
    {
    }

    /// <summary>
    /// A natural key whose lookup is owned by <paramref name="sourceProjectionType" /> when that is not
    /// null; see <see cref="SourceProjectionType" />.
    /// </summary>
    public NaturalKeyDefinition(Type aggregateType, MemberInfo member, Type? sourceProjectionType)
    {
        AggregateType = aggregateType;
        Member = member;
        SourceProjectionType = sourceProjectionType;

        // A struct key declared as TKey? on the aggregate is still a TKey key: the lookup, the fetch
        // APIs and every comparison with another key use the underlying type. GetMemberType() already
        // strips Nullable<T>, so the declared type is read off the member itself.
        var declaredType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => member.GetMemberType()!
        };
        var memberType = KeyTypeOf(declaredType);
        IsNullable = memberType != declaredType;
        OuterType = memberType;

        // Determine if this is a strong-typed id (value type wrapper) or a primitive. A type from the
        // core library is never a strong-typed id, even when it happens to fit the wrapper shape:
        // decimal has one public property (Scale) and an implicit conversion from byte, which made it
        // look like a wrapper around a byte (jasperfx#1272).
        if (IsPrimitiveKeyType(memberType) || memberType.Assembly == typeof(object).Assembly)
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
    /// The projection (or self-aggregating snapshot type) that owns this natural key's lookup, from
    /// <c>[NaturalKey(SourceProjection = ...)]</c> or <c>[MappedNaturalKey&lt;T&gt;]</c>. Null for an owned key.
    /// </summary>
    public Type? SourceProjectionType { get; }

    /// <summary>
    /// True when this key's lookup is owned by another projection (<see cref="SourceProjectionType" />).
    /// A mapped key has no event mappings of its own.
    /// </summary>
    public bool IsMapped => SourceProjectionType != null;

    /// <summary>
    /// The owned natural key definition of the source projection, resolved by the event store once every
    /// projection is registered. Null for an owned key, or before resolution.
    /// </summary>
    public NaturalKeyDefinition? SourceProjectionDefinition { get; internal set; }

    /// <summary>
    /// The aggregate type whose natural key lookup serves this key: the source projection's aggregate
    /// for a mapped key once resolved, otherwise this key's own aggregate.
    /// </summary>
    public Type LookupAggregateType => SourceProjectionDefinition?.AggregateType ?? AggregateType;

    /// <summary>
    /// The outer type of the natural key (may be a strong-typed id wrapper). For a member declared as
    /// <c>Nullable&lt;T&gt;</c> this is <c>T</c>; see <see cref="IsNullable" />.
    /// </summary>
    public Type OuterType { get; }

    /// <summary>
    /// True when the member is declared as <c>Nullable&lt;T&gt;</c> of the key type. It is the same key as a
    /// member of type <c>T</c>; an aggregate whose key is still null simply has no lookup row.
    /// </summary>
    public bool IsNullable { get; }

    /// <summary>
    /// The natural key type a member, event property or <c>[NaturalKeySource]</c> return type of
    /// <paramref name="type" /> stands for: <c>T</c> for <c>Nullable&lt;T&gt;</c>, otherwise the type itself.
    /// </summary>
    public static Type KeyTypeOf(Type type) => Nullable.GetUnderlyingType(type) ?? type;

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
        AddOrReplaceMapping(eventType, extractor, true);
    }

    /// <summary>
    /// <see cref="AddOrReplaceMapping(Type, Func{IEvent, object?})" />, saying whether a null from the extractor
    /// retires the stream's key. See <see cref="NaturalKeyEventMapping.RetiresKeyOnNull" />.
    /// </summary>
    public void AddOrReplaceMapping(Type eventType, Func<IEvent, object?> extractor, bool retiresKeyOnNull)
    {
        EventMappings.RemoveAll(x => x.EventType == eventType);
        EventMappings.Add(new NaturalKeyEventMapping(eventType, extractor, retiresKeyOnNull));
        _problems.RemoveAll(x => x.EventType == eventType);
    }

    /// <summary>
    /// Record a <c>[NaturalKeySource]</c> method that could not be bound to an extractor.
    /// </summary>
    public void RecordProblem(MethodInfo method, Type eventType, string reason)
    {
        RecordProblem(method, eventType, reason, false);
    }

    /// <summary>
    /// <see cref="RecordProblem(MethodInfo, Type, string)" /> for a problem whose reason already carries its fix.
    /// </summary>
    public void RecordProblem(MethodInfo method, Type eventType, string reason, bool hasOwnFix)
    {
        if (HasMappingFor(eventType)) return;

        _problems.Add(new NaturalKeySourceProblem(method, eventType, reason, hasOwnFix));
    }

    /// <summary>
    /// Record a second <c>[NaturalKeySource]</c> method for an event type that already has one. Unlike
    /// <see cref="RecordProblem" /> this is recorded even though the event is mapped; an explicit
    /// registration for the event still clears it.
    /// </summary>
    internal void RecordDuplicateSource(MethodInfo method, Type eventType, string reason)
    {
        _problems.Add(new NaturalKeySourceProblem(method, eventType, reason, true));
    }

    /// <summary>
    /// The key's current value on <paramref name="aggregate" />, unwrapped to its inner primitive
    /// representation as <see cref="Unwrap" /> does; null when the aggregate has no key yet. Lets a store
    /// match an aggregate it already holds -- in an identity map or a cache -- against a natural key.
    /// </summary>
    public object? ReadValue(object aggregate)
    {
        var value = Member switch
        {
            PropertyInfo property => property.GetValue(aggregate),
            FieldInfo field => field.GetValue(aggregate),
            _ => null
        };

        return Unwrap(value);
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
