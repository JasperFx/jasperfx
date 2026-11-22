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

    /// <summary>
    /// Natural keys on this aggregate whose lookup is owned by another projection, configured via
    /// <c>[NaturalKey(SourceProjection = ...)]</c> or <c>[MappedNaturalKey&lt;T&gt;]</c>. Each has a distinct
    /// key type, also distinct from <see cref="NaturalKeyDefinition" />'s.
    /// </summary>
    NaturalKeyDefinition[] MappedNaturalKeyDefinitions { get; }
}