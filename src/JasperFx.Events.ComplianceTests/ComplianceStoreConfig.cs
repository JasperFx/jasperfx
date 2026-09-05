using System;
using System.Collections.Generic;
using JasperFx.Events.Fetching;
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

    /// <summary>
    /// Persist the session's user name (last-modified-by) metadata onto appended events. Off by
    /// default in both products and spelled differently in each — Marten's
    /// <c>Events.MetadataConfig.UserNameEnabled</c>, Polecat's <c>Events.EnableUserName</c> — so the
    /// fixture resolves it, exactly like <see cref="EnableCorrelationTracking"/>. Added for the
    /// jasperfx#737 event query suite.
    /// </summary>
    public bool EnableUserNameTracking { get; set; }

    /// <summary>
    /// Persist per-event header dictionaries. Off by default in both products and spelled
    /// differently in each — Marten's <c>Events.MetadataConfig.HeadersEnabled</c>, Polecat's
    /// <c>Events.EnableHeaders</c> — so the fixture resolves it, exactly like
    /// <see cref="EnableCorrelationTracking"/>.
    /// </summary>
    /// <remarks>
    /// Setting the headers themselves needs nothing from the fixture: <see cref="IEvent.SetHeader"/>
    /// and <see cref="IEvent.GetHeader"/> are on the shared event interface, so a suite builds an
    /// envelope with <c>BuildEvent</c>, stamps it and appends it.
    /// </remarks>
    public bool EnableHeaders { get; set; }

    /// <summary>
    /// Slice the event store by tenant within one database — "conjoined" tenancy, where every
    /// stream and event carries a tenant id and reads are scoped to one tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only seam this needs. Both products spell the setting identically —
    /// <c>Events.TenancyStyle = TenancyStyle.Conjoined</c>, over the <em>shared</em>
    /// <c>JasperFx.MultiTenancy.TenancyStyle</c> enum — but on their own event options rather than
    /// on any shared interface, so it has to come through the registrar.
    /// </para>
    /// <para>
    /// Opening a tenant-scoped session needs nothing: <c>IEventStore&lt;TOperations,
    /// TQuerySession&gt;.OpenSession(IEventDatabase, string tenantId)</c> is already on the shared
    /// generic interface and implemented by both stores.
    /// </para>
    /// </remarks>
    public bool ConjoinedEventTenancy { get; set; }

    public List<Type> EventTypes { get; } = new();

    public List<(Type Tag, string Suffix, Type? Aggregate)> TagTypes { get; } = new();

    public List<(Type Doc, SnapshotLifecycle Lifecycle)> Snapshots { get; } = new();

    public List<Type> LiveAggregations { get; } = new();

    /// <summary>
    /// Strong-typed identifier wrappers the suite asked the store to register.
    /// </summary>
    public List<Type> ValueTypes { get; } = new();

    public List<(ProjectionBase Projection, ProjectionLifecycle Lifecycle)> Projections { get; } = new();

    public ComplianceStoreConfig AddEventType<T>()
    {
        EventTypes.Add(typeof(T));
        _registrations.Add(registrar => registrar.AddEventType(typeof(T)));
        return this;
    }

    /// <summary>
    /// Event types the suite asked to be stored through a binary serializer, with the serializer.
    /// </summary>
    public List<(Type Event, IEventBinarySerializer Serializer)> BinarySerializers { get; } = new();

    /// <summary>
    /// The store-wide fallback serializer, when the suite set one.
    /// </summary>
    public IEventBinarySerializer? DefaultBinarySerializer { get; private set; }

    public ComplianceStoreConfig UseBinarySerializer<TEvent>(IEventBinarySerializer serializer)
        where TEvent : notnull
    {
        BinarySerializers.Add((typeof(TEvent), serializer));
        _registrations.Add(registrar => registrar.UseBinarySerializer<TEvent>(serializer));
        return this;
    }

    public ComplianceStoreConfig SetDefaultBinarySerializer(IEventBinarySerializer serializer)
    {
        DefaultBinarySerializer = serializer;
        _registrations.Add(registrar => registrar.SetDefaultBinarySerializer(serializer));
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

    /// <summary>
    /// Aggregate types the suite enrolled in the <c>FetchForWriting</c> snapshot cache, with the
    /// cache instance it wants the store to use.
    /// </summary>
    public List<(Type Doc, IAggregateWriteCache Cache)> CachedAggregates { get; } = new();

    /// <summary>
    /// Enroll an aggregate type in the second-level <c>FetchForWriting</c> snapshot cache. Off for
    /// every type unless asked for, which is the behavior
    /// <see cref="AggregateWriteCacheCompliance{TFixture,TOperations,TQuerySession}" /> asserts.
    /// </summary>
    /// <remarks>
    /// The cache instance is supplied by the suite rather than left to the store so the suite can
    /// see what the store actually did with it — a store that quietly ignored the opt-in would
    /// otherwise pass every behavioral fact, since an uncached fetch is correct by construction.
    /// </remarks>
    public ComplianceStoreConfig CacheAggregatesForWriting<TDoc>(IAggregateWriteCache cache)
        where TDoc : class
    {
        CachedAggregates.Add((typeof(TDoc), cache));
        _registrations.Add(registrar => registrar.CacheAggregatesForWriting<TDoc>(cache));
        return this;
    }

    /// <summary>
    /// Register a strong-typed identifier wrapper with the store.
    /// </summary>
    /// <remarks>
    /// A no-op on stores that resolve value types automatically. Marten requires explicit
    /// registration through <c>StoreOptions.RegisterValueType&lt;T&gt;()</c>; Polecat discovers the
    /// same shape via <c>ValueTypeInfo</c> when the document mapping is built and exposes no
    /// equivalent call. Same shape as <see cref="LiveAggregation{TDoc}"/>, which exists for the
    /// mirror-image reason.
    /// </remarks>
    public ComplianceStoreConfig RegisterValueType<TValue>() where TValue : notnull
    {
        ValueTypes.Add(typeof(TValue));
        _registrations.Add(registrar => registrar.RegisterValueType<TValue>());
        return this;
    }

    /// <summary>
    /// Register a mutating masking rule for an event type.
    /// </summary>
    /// <remarks>
    /// Both products spell this <c>Events.AddMaskingRuleForProtectedInformation&lt;T&gt;</c> with
    /// identical signatures, but on their own event options rather than on any shared interface,
    /// which is the whole reason this seam member exists.
    /// </remarks>
    public ComplianceStoreConfig AddMaskingRule<TEvent>(Action<TEvent> rule) where TEvent : notnull
    {
        _registrations.Add(registrar => registrar.AddMaskingRule(rule));
        return this;
    }

    /// <summary>
    /// Register a replacing masking rule for an event type — the form a <c>record</c> needs.
    /// </summary>
    /// <inheritdoc cref="AddMaskingRule{TEvent}(Action{TEvent})" path="/remarks"/>
    public ComplianceStoreConfig AddMaskingRule<TEvent>(Func<TEvent, TEvent> rule) where TEvent : notnull
    {
        _registrations.Add(registrar => registrar.AddMaskingRule(rule));
        return this;
    }

    /// <summary>
    /// Composite projections the suite asked the store to build, by name.
    /// </summary>
    public List<string> CompositeProjections { get; } = new();

    /// <summary>
    /// Build a composite projection with the given name and populate its stages.
    /// </summary>
    /// <inheritdoc cref="IComplianceStoreRegistrar.AddCompositeProjection" path="/remarks"/>
    public ComplianceStoreConfig AddCompositeProjection(string name, Action<IComplianceCompositeBuilder> configure)
    {
        CompositeProjections.Add(name);
        _registrations.Add(registrar => registrar.AddCompositeProjection(name, configure));
        return this;
    }

    /// <summary>
    /// Event upcast transformations the suite asked the store to register.
    /// </summary>
    public List<JasperFx.Events.Upcasting.UpcastTransformation> Upcasts { get; } = new();

    /// <summary>
    /// Register an event upcast transformation.
    /// </summary>
    /// <inheritdoc cref="IComplianceStoreRegistrar.Upcast" path="/remarks"/>
    public ComplianceStoreConfig Upcast(JasperFx.Events.Upcasting.UpcastTransformation transformation)
    {
        Upcasts.Add(transformation);
        _registrations.Add(registrar => registrar.Upcast(transformation));
        return this;
    }

    /// <summary>
    /// Register the shared compliance subscription with the store's async daemon.
    /// </summary>
    /// <inheritdoc cref="IComplianceStoreRegistrar.Subscribe" path="/remarks"/>
    public ComplianceStoreConfig Subscribe(ComplianceSubscription subscription)
    {
        _registrations.Add(registrar => registrar.Subscribe(subscription));
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
