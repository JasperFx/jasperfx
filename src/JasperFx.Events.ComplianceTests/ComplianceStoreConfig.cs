using System;
using System.Collections.Generic;
using JasperFx.Events.Projections;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Store-neutral description of the event store configuration a compliance suite needs.
/// A suite fills one of these in; the fixture replays it against its store through
/// <see cref="IComplianceStoreRegistrar"/>.
/// </summary>
/// <remarks>
/// The public lists exist so a fixture can inspect what was asked for (for example, to decide
/// whether the schema needs event tag tables). The actual registration runs through the recorded
/// generic closures in <see cref="ApplyTo"/>, in declaration order.
/// </remarks>
public sealed class ComplianceStoreConfig
{
    private readonly List<Action<IComplianceStoreRegistrar>> _registrations = new();

    /// <summary>
    /// Optional schema/namespace override. When null the fixture picks its own.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// Optional explicit value for the per-database rebuild concurrency cap. Null leaves the store
    /// on its derived default; zero or negative disables the cap.
    /// </summary>
    /// <remarks>
    /// Not routed through <see cref="IComplianceStoreRegistrar"/> because the products hang the knob
    /// off different option objects (Marten <c>Projections</c>, Polecat <c>DaemonSettings</c>) and
    /// the fixture is already the place that knows which.
    /// </remarks>
    public int? MaxConcurrentRebuildsPerDatabase { get; set; }

    /// <summary>
    /// Optional connection pool ceiling, folded into the connection string by the fixture. Exists so
    /// the rebuild-cap suite can exercise the pool-size-derived default without caring whether the
    /// store speaks Npgsql or SqlClient.
    /// </summary>
    public int? MaxPoolSize { get; set; }

    /// <summary>
    /// Optional stream identity style. Null leaves the store on its own default, which is
    /// <see cref="Events.StreamIdentity.AsGuid"/> in both products.
    /// </summary>
    /// <remarks>
    /// A plain property rather than an <see cref="IComplianceStoreRegistrar"/> call for the same
    /// reason as <see cref="MaxConcurrentRebuildsPerDatabase"/>: the value is the shared
    /// <see cref="Events.StreamIdentity"/> enum, but the options object it hangs off is the product's
    /// own event graph, and the fixture is already the place that knows which.
    /// </remarks>
    public StreamIdentity? StreamIdentity { get; set; }

    /// <summary>
    /// Persist correlation and causation metadata onto appended events. Off by default in both
    /// products, and spelled differently in each — Marten's <c>Events.MetadataConfig</c>, Polecat's
    /// <c>Events.EnableCorrelationId</c>/<c>EnableCausationId</c>.
    /// </summary>
    /// <remarks>
    /// One flag rather than two because no suite needs correlation without causation, and the pair
    /// is what distributed tracing actually populates.
    /// </remarks>
    public bool EnableCorrelationTracking { get; set; }

    public List<Type> EventTypes { get; } = new();

    public List<(Type Tag, string Suffix, Type? Aggregate)> TagTypes { get; } = new();

    public List<(Type Doc, SnapshotLifecycle Lifecycle)> Snapshots { get; } = new();

    public List<Type> LiveAggregations { get; } = new();

    public List<(ProjectionBase Projection, ProjectionLifecycle Lifecycle)> Projections { get; } = new();

    public ComplianceStoreConfig AddEventType<T>()
    {
        EventTypes.Add(typeof(T));
        _registrations.Add(registrar => registrar.AddEventType(typeof(T)));
        return this;
    }

    public ComplianceStoreConfig RegisterTagType<TTag>(string tableSuffix, Type? aggregateType = null)
        where TTag : notnull
    {
        TagTypes.Add((typeof(TTag), tableSuffix, aggregateType));
        _registrations.Add(registrar =>
        {
            var registration = registrar.RegisterTagType<TTag>(tableSuffix);
            if (aggregateType != null)
            {
                registration.ForAggregate(aggregateType);
            }
        });

        return this;
    }

    public ComplianceStoreConfig Snapshot<TDoc>(SnapshotLifecycle lifecycle) where TDoc : notnull
    {
        Snapshots.Add((typeof(TDoc), lifecycle));
        _registrations.Add(registrar => registrar.Snapshot<TDoc>(lifecycle));
        return this;
    }

    public ComplianceStoreConfig LiveAggregation<TDoc>() where TDoc : notnull
    {
        LiveAggregations.Add(typeof(TDoc));
        _registrations.Add(registrar => registrar.LiveAggregation<TDoc>());
        return this;
    }

    /// <summary>
    /// Register an already-constructed projection instance. Used where the projection carries test
    /// state (the enrichment suite's call-order recorder) or where the point of the test is what the
    /// source generator emitted onto a concrete projection type.
    /// </summary>
    public ComplianceStoreConfig AddProjection(ProjectionBase projection, ProjectionLifecycle lifecycle)
    {
        Projections.Add((projection, lifecycle));
        _registrations.Add(registrar => registrar.AddProjection(projection, lifecycle));
        return this;
    }

    public void ApplyTo(IComplianceStoreRegistrar registrar)
    {
        foreach (var registration in _registrations)
        {
            registration(registrar);
        }
    }
}
