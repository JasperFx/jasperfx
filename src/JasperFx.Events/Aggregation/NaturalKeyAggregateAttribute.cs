namespace JasperFx.Events.Aggregation;

/// <summary>
/// Assembly-level attribute emitted by the source generator to indicate that a self-aggregating
/// type has a [NaturalKey] property. This allows event store implementations (e.g., Marten)
/// to auto-register the snapshot projection and natural key infrastructure at startup,
/// without requiring the user to explicitly call Projections.Snapshot&lt;T&gt;(Inline).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class NaturalKeyAggregateAttribute : Attribute
{
    public NaturalKeyAggregateAttribute(Type aggregateType)
    {
        AggregateType = aggregateType;
    }

    public Type AggregateType { get; }
}
