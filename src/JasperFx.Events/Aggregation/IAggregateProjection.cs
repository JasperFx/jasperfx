using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public interface IAggregateProjection
{
    Type IdentityType { get; }
    Type AggregateType { get; }

    ProjectionLifecycle Lifecycle { get; }

    AggregationScope Scope { get; }

    uint Version { get; }

    Type[] AllEventTypes { get; }

    AsyncOptions Options { get; }

    /// <summary>
    /// The natural key definition for this aggregate, if one is configured via [NaturalKey] attribute.
    /// </summary>
    NaturalKeyDefinition? NaturalKeyDefinition { get; }
}