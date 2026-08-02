using System;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The narrow, store-neutral configuration surface that a <see cref="ComplianceStoreConfig"/>
/// is replayed against. Each event store implements this once inside its concrete
/// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}"/> — it is the only place
/// where store-specific option types (Marten's <c>StoreOptions</c>, Polecat's equivalent) leak in.
/// </summary>
/// <remarks>
/// Registration is expressed through generic methods rather than <see cref="Type"/> arguments so
/// that nothing in the compliance library needs reflection: the suites capture the closed generic
/// at the call site and the registrar re-opens it here.
/// </remarks>
public interface IComplianceStoreRegistrar
{
    /// <summary>
    /// Pre-register an event type. Maps to the shared <c>IEventRegistry.AddEventType(Type)</c>.
    /// </summary>
    void AddEventType(Type eventType);

    /// <summary>
    /// Register a strong-typed identifier as a DCB tag type with an explicit table suffix.
    /// </summary>
    ITagTypeRegistration RegisterTagType<TTag>(string tableSuffix) where TTag : notnull;

    /// <summary>
    /// Register a single stream snapshot projection for a self-aggregating type.
    /// </summary>
    void Snapshot<TDoc>(SnapshotLifecycle lifecycle) where TDoc : notnull;

    /// <summary>
    /// Register a live aggregation for a self-aggregating type. A no-op in stores that
    /// build live aggregators automatically.
    /// </summary>
    void LiveAggregation<TDoc>() where TDoc : notnull;

    /// <summary>
    /// Register an already-constructed projection instance.
    /// </summary>
    /// <remarks>
    /// Typed as the shared <see cref="ProjectionBase"/> rather than
    /// <c>IProjectionSource&lt;TOperations, TQuerySession&gt;</c> because this interface is not
    /// generic over the session pair; the implementing fixture casts down to its own closure. Every
    /// projection a suite can build derives from the product's own EventProjection base, so the cast
    /// is total in practice.
    /// </remarks>
    void AddProjection(ProjectionBase projection, ProjectionLifecycle lifecycle);
}
