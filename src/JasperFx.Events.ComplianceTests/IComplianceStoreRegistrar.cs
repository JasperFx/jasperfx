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
    /// Register a strong-typed identifier wrapper — a type with a single public gettable property
    /// and either a matching constructor or a static builder taking that property's type.
    /// </summary>
    /// <remarks>
    /// Implement as a no-op where the store discovers value types automatically. The asymmetry is
    /// real rather than incidental: Marten needs the type registered before it can use it in LINQ
    /// and identity mapping, while Polecat derives the same information from
    /// <c>ValueTypeInfo</c> when it builds the document mapping.
    /// </remarks>
    void RegisterValueType<TValue>() where TValue : notnull;

    /// <summary>
    /// Register a mutating masking rule for an event type — the in-place form, for events whose
    /// protected members are settable.
    /// </summary>
    /// <remarks>
    /// Both products spell this <c>Events.AddMaskingRuleForProtectedInformation&lt;T&gt;</c> with
    /// identical signatures, but on their own event options rather than on any shared interface, so
    /// it has to come through the registrar. Rules are contravariant on both: a rule registered
    /// against an interface applies to every event type implementing it.
    /// </remarks>
    void AddMaskingRule<TEvent>(Action<TEvent> rule) where TEvent : notnull;

    /// <summary>
    /// Register a replacing masking rule for an event type — the functional form, which is what a
    /// <c>record</c> with init-only members needs.
    /// </summary>
    /// <inheritdoc cref="AddMaskingRule{TEvent}(Action{TEvent})" path="/remarks"/>
    void AddMaskingRule<TEvent>(Func<TEvent, TEvent> rule) where TEvent : notnull;

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

    /// <summary>
    /// Register the shared compliance subscription with the store's async daemon.
    /// </summary>
    /// <remarks>
    /// Typed as the concrete <see cref="ComplianceSubscription"/>, which is unusual for this
    /// interface and is deliberate. Neither product exposes a public <c>Subscribe</c> overload
    /// taking the shared <c>ISubscriptionSource&lt;TOperations, TQuerySession&gt;</c> — both take
    /// their own <c>ISubscription</c>, and their <c>registerSubscription</c> is private — so there
    /// is no shared type to accept here. The library owns <c>ComplianceSubscription</c> and each
    /// consumer completes it as a partial implementing its own interface, so naming the concrete
    /// type is the one spelling that works on both. Implementations should pin the subscription
    /// name to <see cref="ComplianceSubscription.SubscriptionName"/>.
    /// </remarks>
    void Subscribe(ComplianceSubscription subscription);
}
