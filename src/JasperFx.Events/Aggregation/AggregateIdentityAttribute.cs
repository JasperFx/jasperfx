namespace JasperFx.Events.Aggregation;

/// <summary>
/// Specify the identity type for a self-aggregating type when the source generator
/// cannot infer it from an Id property.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class AggregateIdentityAttribute : Attribute
{
    public AggregateIdentityAttribute(Type identityType) => IdentityType = identityType;
    public Type IdentityType { get; }
}
