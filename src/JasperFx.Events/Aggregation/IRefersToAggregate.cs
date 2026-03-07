namespace JasperFx.Events.Aggregation;

/// <summary>
/// Marker interface for attributes that refer to an aggregate type.
/// When applied to attributes on method parameters, the source generator
/// will discover and generate evolvers for the parameter's aggregate type,
/// ensuring Marten and Polecat can use source-generated projections
/// even without explicit Snapshot&lt;T&gt;() registration.
/// </summary>
public interface IRefersToAggregate;
