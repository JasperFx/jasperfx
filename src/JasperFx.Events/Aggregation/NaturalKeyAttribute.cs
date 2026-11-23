using System;

namespace JasperFx.Events.Aggregation;

/// <summary>
/// Marks a property on an aggregate type as a natural key for the event stream.
/// The natural key provides an alternative lookup for FetchForWriting/FetchLatest
/// using a domain-meaningful identifier alongside the surrogate stream id.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class NaturalKeyAttribute : Attribute
{
    /// <summary>
    /// Optional reference to another projection that owns and maintains the natural key lookup.
    /// This is either that projection's class or, for a self-aggregating snapshot, its aggregate type.
    /// When set, this aggregate does not maintain a lookup of its own; fetching by this key resolves the
    /// stream through the source projection's lookup instead. This is how an async projection becomes
    /// fetchable by natural key: the inline owner keeps the lookup current while the async snapshot
    /// catches up. A key with no source projection is an <i>owned</i> key, and an aggregate can have
    /// at most one of those.
    /// </summary>
    public Type? SourceProjection { get; set; }
}

/// <summary>
/// Marks a property on an aggregate type as a natural key that is owned and maintained by
/// <typeparamref name="TSourceProjection" />. Equivalent to
/// <c>[NaturalKey(SourceProjection = typeof(TSourceProjection))]</c>.
/// </summary>
/// <typeparam name="TSourceProjection">
/// The projection class, or self-aggregating snapshot type, that owns the natural key lookup.
/// </typeparam>
public class MappedNaturalKeyAttribute<TSourceProjection> : NaturalKeyAttribute
{
    public MappedNaturalKeyAttribute()
    {
        SourceProjection = typeof(TSourceProjection);
    }
}

/// <summary>
/// Marks an Apply/Create method on a self-aggregating snapshot as a source
/// of natural key values. The method's event parameter type will be registered
/// as a natural key event mapping.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class NaturalKeySourceAttribute : Attribute;
