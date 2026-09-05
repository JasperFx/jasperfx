using System;
using JasperFx.Events.Fetching;
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
    /// Register a binary serializer for one event type. Maps to each store's
    /// <c>Events.UseBinarySerializer&lt;TEvent&gt;(serializer)</c>.
    /// </summary>
    /// <remarks>
    /// Carries a throwing default because binary event serialization is an opt-in capability rather
    /// than part of the baseline event contract — a store that has not implemented it does not
    /// enroll in <see cref="BinaryEventSerializationCompliance{TFixture}" />, never reaches this
    /// member, and keeps compiling against a newer compliance package.
    /// </remarks>
    void UseBinarySerializer<TEvent>(IEventBinarySerializer serializer) where TEvent : notnull
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement UseBinarySerializer, so it cannot run the binary event serialization compliance suite.");

    /// <summary>
    /// Set the store-wide fallback serializer used by event types marked with
    /// <see cref="BinaryEventAttribute" /> that have no explicit per-type registration.
    /// </summary>
    /// <inheritdoc cref="UseBinarySerializer{TEvent}" path="/remarks" />
    void SetDefaultBinarySerializer(IEventBinarySerializer serializer)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement SetDefaultBinarySerializer, so it cannot run the binary event serialization compliance suite.");

    /// <summary>
    /// Register a single stream snapshot projection for a self-aggregating type.
    /// </summary>
    void Snapshot<TDoc>(SnapshotLifecycle lifecycle) where TDoc : notnull;

    /// <summary>
    /// Enroll an aggregate type in the second-level <c>FetchForWriting</c> snapshot cache, using the
    /// supplied cache instance so the suite can observe it. Maps to the shared
    /// <c>EventRegistry.CacheAggregatesForWriting&lt;T&gt;()</c> plus
    /// <c>EventRegistry.AggregateWriteCaching.Cache</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are on the shared <see cref="EventRegistry" />, so an implementation is two
    /// lines. It still comes through the registrar because the fixture is the only thing that knows
    /// which options object the store hangs its event registry off.
    /// </para>
    /// <para>
    /// Carries a throwing default for the same reason as
    /// <see cref="UseBinarySerializer{TEvent}" />: caching is an opt-in capability rather than part
    /// of the baseline contract, so a store that has not wired it into its fetch plans does not
    /// enroll in <see cref="AggregateWriteCacheCompliance{TFixture,TOperations,TQuerySession}" />,
    /// never reaches this member, and keeps compiling against a newer compliance package.
    /// </para>
    /// </remarks>
    void CacheAggregatesForWriting<TDoc>(IAggregateWriteCache cache) where TDoc : class
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement CacheAggregatesForWriting, so it cannot run the aggregate write cache compliance suite.");

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
    /// Build a composite projection with the given name and populate its stages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Routed through the registrar rather than <see cref="AddProjection" /> because a composite
    /// cannot be constructed by a suite. Every product subclasses the shared
    /// <c>CompositeProjection&lt;TOperations,TQuerySession&gt;</c> and keeps the constructor internal —
    /// it needs the store's options — so a composite only ever comes into being through that product's
    /// own <c>Projections.CompositeProjectionFor(name, configure)</c>, whose <c>configure</c> parameter
    /// is typed to the product's own subclass.
    /// </para>
    /// <para>
    /// The implementation is a forward plus an adapter, because the *calls* are identical across the
    /// products even though the types are not — all three expose <c>Snapshot&lt;T&gt;(int stageNumber)</c>
    /// on their composite (Marten returns a mapping expression, Polecat and Fisher return void, which is
    /// why <see cref="IComplianceCompositeBuilder" /> declares its own void-returning member rather than
    /// trying to name a shared return type):
    /// </para>
    /// <code>
    /// public void AddCompositeProjection(string name, Action&lt;IComplianceCompositeBuilder&gt; configure)
    ///     => _options.Projections.CompositeProjectionFor(name, c => configure(new Builder(c)));
    /// </code>
    /// <para>
    /// Carries a throwing default for the same reason as <see cref="UseBinarySerializer{TEvent}" />: a
    /// store that has not implemented composites does not enroll in
    /// <see cref="CompositeProjectionCompliance{TFixture,TOperations,TQuerySession}" />, never reaches
    /// this member, and keeps compiling. See
    /// <see href="https://github.com/JasperFx/jasperfx/issues/725" />.
    /// </para>
    /// </remarks>
    void AddCompositeProjection(string name, Action<IComplianceCompositeBuilder> configure)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement AddCompositeProjection, so it cannot run the composite projection compliance suite.");

    /// <summary>
    /// Register an event upcast transformation. Maps to the shared
    /// <c>EventRegistry.Upcasters.Register(transformation)</c>, plus whatever per-store wiring
    /// makes the store's read path honor it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One member covers every registration shape — typed sync, typed async-only, raw
    /// <c>JsonDocument</c> — because <c>JasperFx.Events.Upcasting.UpcastTransformation</c> is the
    /// shared carrier all of them funnel into; the suite builds transformations through the shared
    /// factories, so the registrar never needs the generic overload family.
    /// </para>
    /// <para>
    /// Carries a throwing default for the same reason as <see cref="UseBinarySerializer{TEvent}" />:
    /// upcasting is gated (<c>SupportsUpcasting</c>, default false, see jasperfx#752), so a store
    /// that has not implemented it never reaches this member and keeps compiling against a newer
    /// compliance package.
    /// </para>
    /// </remarks>
    void Upcast(JasperFx.Events.Upcasting.UpcastTransformation transformation)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement Upcast, so it cannot run the event upcasting compliance suite.");

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
