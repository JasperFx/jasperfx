namespace JasperFx.Events.Aggregation;

/// <summary>
/// Assembly-level attribute emitted by the source generator to register a generated evolver
/// for a self-aggregating type or an aggregation projection subclass.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class GeneratedEvolverAttribute : Attribute
{
    public GeneratedEvolverAttribute(Type aggregateType, Type evolverType)
    {
        AggregateType = aggregateType;
        EvolverType = evolverType;
    }

    /// <summary>
    /// Registers an evolver that is specific to a particular projection subclass. Several distinct
    /// projections can target the same aggregate type with different dispatch logic, so an evolver
    /// emitted for a projection subclass must only ever bind to that projection — not to any other
    /// projection (or no-op projection) that happens to share the aggregate type. Self-aggregating
    /// evolvers leave <see cref="ProjectionType"/> null and bind by aggregate type alone. See #462.
    /// </summary>
    public GeneratedEvolverAttribute(Type aggregateType, Type evolverType, Type projectionType)
        : this(aggregateType, evolverType)
    {
        ProjectionType = projectionType;
    }

    public Type AggregateType { get; }
    public Type EvolverType { get; }

    /// <summary>
    /// The concrete projection subclass this evolver dispatches to, or null for a self-aggregating
    /// evolver that binds by aggregate type alone.
    /// </summary>
    public Type? ProjectionType { get; }
}
