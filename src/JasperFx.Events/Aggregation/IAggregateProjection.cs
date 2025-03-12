using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public interface IAggregateProjection
{
    Type IdentityType { get; }
    Type AggregateType { get; }

    ProjectionLifecycle Lifecycle { get; }

    AggregationScope Scope { get; }

    uint ProjectionVersion { get; }

    Type[] AllEventTypes { get; }

    AsyncOptions Options { get; }
}