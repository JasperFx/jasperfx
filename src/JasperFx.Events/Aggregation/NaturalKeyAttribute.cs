using System;

namespace JasperFx.Events.Aggregation;

/// <summary>
/// Marks a property on an aggregate type as a natural key for the event stream.
/// The natural key provides an alternative lookup for FetchForWriting/FetchLatest
/// using a domain-meaningful identifier alongside the surrogate stream id.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class NaturalKeyAttribute : Attribute;

/// <summary>
/// Marks an Apply/Create method on a self-aggregating snapshot as a source
/// of natural key values. The method's event parameter type will be registered
/// as a natural key event mapping.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class NaturalKeySourceAttribute : Attribute;
