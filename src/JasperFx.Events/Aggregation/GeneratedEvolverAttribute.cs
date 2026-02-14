namespace JasperFx.Events.Aggregation;

/// <summary>
/// Assembly-level attribute emitted by the source generator to register a generated evolver
/// for a self-aggregating type.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class GeneratedEvolverAttribute : Attribute
{
    public GeneratedEvolverAttribute(Type aggregateType, Type evolverType)
    {
        AggregateType = aggregateType;
        EvolverType = evolverType;
    }

    public Type AggregateType { get; }
    public Type EvolverType { get; }
}
