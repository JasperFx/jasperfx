using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The seam between the shared event sourcing compliance suites and a concrete event store.
/// Every portable operation in the suites flows through the shared JasperFx surfaces
/// (<see cref="IEventStoreOperations"/>, <see cref="IEventRegistry"/>, <see cref="IProjectionDaemon"/>);
/// this type only exists to absorb the handful of things no shared interface declares —
/// store construction, session acquisition, SaveChanges, document load-back, batched DCB queries,
/// and teardown.
/// </summary>
/// <typeparam name="TOperations">
/// The store's writable session type — Marten <c>IDocumentOperations</c>, Polecat <c>IDocumentSession</c>.
/// </typeparam>
/// <typeparam name="TQuerySession">The store's read-only session type.</typeparam>
/// <remarks>
/// The generic closure mirrors JasperFx's own <c>IEventStore&lt;TOperations, TQuerySession&gt;</c>.
/// The products deliberately close it differently and convergence is a non-goal, so the compliance
/// library is generic over the same pair rather than trying to unify them.
/// </remarks>
public abstract class EventStoreComplianceFixture<TOperations, TQuerySession> : IAsyncLifetime
    where TOperations : TQuerySession, IStorageOperations
{
    private Action<ComplianceStoreConfig>? _lastConfiguration;

    /// <summary>
    /// The most recently built store-neutral configuration — what
    /// <see cref="StartCoordinatorHostAsync(bool)"/> replays into a hosted store so the suite's
    /// projections are registered there as well as on the fixture's own store.
    /// </summary>
    protected ComplianceStoreConfig? CurrentConfig { get; private set; }

    /// <summary>
    /// Cancellation token handed to every store call the suites make. Overridable rather than
    /// hard-coded so a consumer can swap in its own budget.
    /// </summary>
    public virtual CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Build (or rebuild) the store for the supplied configuration.
    /// </summary>
    /// <remarks>
    /// Deliberately keyed on the identity of the <paramref name="configure"/> delegate: suites hold
    /// their standard configuration in a static field, so repeated calls across the test methods of
    /// one class are free, while a test that deliberately passes a different delegate gets a real
    /// rebuild. Async because some stores (Polecat) must apply schema changes explicitly.
    /// </remarks>
    public async Task ConfigureAsync(Action<ComplianceStoreConfig> configure)
    {
        if (ReferenceEquals(_lastConfiguration, configure))
        {
            return;
        }

        var config = new ComplianceStoreConfig();
        configure(config);

        await BuildStoreAsync(config).ConfigureAwait(false);

        CurrentConfig = config;
        _lastConfiguration = configure;
    }

    /// <summary>
    /// Construct the store from the store-neutral configuration, replaying it through an
    /// <see cref="IComplianceStoreRegistrar"/>, and make sure its schema exists.
    /// </summary>
    protected abstract Task BuildStoreAsync(ComplianceStoreConfig config);

    /// <summary>
    /// Open a writable session. Callers dispose it — <see cref="IStorageOperations"/> is
    /// <see cref="IAsyncDisposable"/> on both stores.
    /// </summary>
    public abstract TOperations OpenSession();

    public abstract Task SaveChangesAsync(TOperations session, CancellationToken token);

    /// <summary>
    /// Load a persisted document by id. Distinct from re-folding a stream: this is what proves an
    /// inline snapshot projection actually wrote something.
    /// </summary>
    public abstract Task<T?> LoadDocumentAsync<T>(TQuerySession session, object id, CancellationToken token)
        where T : class;

    /// <summary>
    /// Store a plain document — not an event. Only needed where a suite has to seed state the event
    /// store itself did not produce, such as the lookup document an enrichment projection reads.
    /// </summary>
    public abstract void StoreDocument<T>(TOperations session, T document) where T : notnull;

    /// <summary>
    /// The payoff member — everything portable in the suites runs off the shared JasperFx surface.
    /// </summary>
    public abstract IEventStoreOperations EventsFor(TOperations session);

    /// <summary>
    /// The session's correlation id, which both products seed from <c>Activity.Current.RootId</c>
    /// when the session opens.
    /// </summary>
    /// <remarks>
    /// Session-scoped correlation/causation is one of the few genuinely shared behaviors that no
    /// shared interface declares: both products hang the pair off their own query session type, and
    /// <see cref="IStorageOperations"/> deliberately stays narrow. Three fixture members are cheaper
    /// than widening that contract. The <em>event</em> side of the same behavior needs nothing here,
    /// because <see cref="IEvent.CorrelationId"/> is already shared.
    /// </remarks>
    public abstract string? CorrelationIdFor(TOperations session);

    public abstract string? CausationIdFor(TOperations session);

    /// <summary>
    /// Assign the correlation id explicitly, which must beat whatever the ambient activity seeded.
    /// </summary>
    public abstract void SetCorrelationId(TOperations session, string? correlationId);

    /// <summary>
    /// Assign the session's user name (last-modified-by) metadata, which the store stamps onto
    /// appended events when user name metadata is enabled.
    /// </summary>
    /// <remarks>
    /// Exists for the same reason as <see cref="SetCorrelationId"/>: both products hang the member
    /// off their own session type and <see cref="IStorageOperations"/> deliberately stays narrow.
    /// Added for the jasperfx#737 event query suite, which filters on the user name column.
    /// </remarks>
    public abstract void SetUserName(TOperations session, string? userName);

    /// <summary>
    /// The store itself, as the shared <see cref="IEventStore"/> surface. Suites reach for this on
    /// store-level contracts — the rebuild concurrency cap, usage descriptors — never for anything
    /// session-scoped.
    /// </summary>
    public abstract IEventStore EventStore { get; }

    /// <summary>
    /// Aggregate types the store knows about, including ones discovered from source-generated
    /// evolvers rather than explicit registration.
    /// </summary>
    /// <remarks>
    /// <c>ProjectionGraph.AllAggregateTypes()</c> is shared, but the graph hangs off each product's
    /// own options type, so reaching it costs one line of fixture code.
    /// </remarks>
    public abstract IEnumerable<Type> AllAggregateTypes();

    public abstract IComplianceBatch CreateBatch(TQuerySession session);

    /// <summary>
    /// The store's event registry, reached through the shared interface so assertions can use
    /// <c>EventMappingFor(Type)</c> → <see cref="IEventType"/> with no InternalsVisibleTo.
    /// </summary>
    public abstract IEventRegistry Registry { get; }

    /// <summary>
    /// Per-test isolation: remove all event (and projected document) data without dropping schema.
    /// </summary>
    public abstract Task CleanEventDataAsync();

    public abstract Task<IProjectionDaemon> StartDaemonAsync();

    public abstract Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout);

    /// <summary>
    /// Read every row of a flat-table projection's table, as case-insensitive column/value maps.
    /// </summary>
    /// <param name="tableName">Unqualified table name. The fixture resolves the schema.</param>
    /// <remarks>
    /// <para>
    /// The only raw data-access member on this seam, and deliberately the narrowest one that works:
    /// a table name in, every row out. No predicates, no ordering, no SQL from the suite. A flat
    /// table is by definition not a document, so there is no supported read path for its rows on
    /// either product — asserting the result of a flat-table projection means reading the table, and
    /// that is dialect-specific in a way nothing shared can absorb (schema resolution, identifier
    /// quoting, parameter syntax).
    /// </para>
    /// <para>
    /// Keeping it predicate-free is the point. Suites filter in memory on the identity they appended
    /// under, so this never becomes a general query escape hatch — the moment it grows a where
    /// clause it starts encoding one dialect's expression syntax and stops being portable.
    /// </para>
    /// <para>
    /// Column keys must compare case-insensitively: PostgreSQL folds undelimited identifiers to
    /// lower case while SQL Server preserves the declared casing, so a suite that asked for
    /// <c>row["member_count"]</c> would otherwise pass on one store and fail on the other for
    /// reasons that have nothing to do with the projection.
    /// </para>
    /// </remarks>
    public abstract Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryTableAsync(
        string tableName, CancellationToken token);

    /// <summary>
    /// Execute the store's raw-event LINQ query — <c>QueryAllRawEvents()</c> on both products —
    /// with <paramref name="filter"/> applied in a single <c>Where()</c> call, and return the
    /// matched events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seam member because the LINQ-returning methods are deliberately off
    /// <see cref="IQueryEventStore"/> — they return product-specific queryable types. The shape is
    /// the narrowest that works, on the same reasoning as <see cref="QueryTableAsync"/>: one
    /// predicate over the shared <see cref="IEvent"/>, one <c>Where()</c>, executed. No ordering, no
    /// paging, no queryable handed back — the moment this grows operators it starts pinning one
    /// provider's LINQ surface, which the library keeps out of scope permanently. The single-Where
    /// contract is itself load-bearing for the HasTag suite: its facts assert that a tag predicate
    /// composes with ordinary event predicates <em>inside one predicate tree</em>, which two chained
    /// <c>Where()</c> calls would not exercise.
    /// </para>
    /// <para>
    /// Carries a throwing default because HasTag-in-LINQ is an opt-in capability; the suite gates on
    /// <see cref="SupportsHasTagLinqPredicates"/> and skips until a store implements this pair and
    /// flips the flag.
    /// </para>
    /// </remarks>
    public virtual Task<IReadOnlyList<IEvent>> QueryRawEventsAsync(TQuerySession session,
        Expression<Func<IEvent, bool>> filter, CancellationToken token)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement QueryRawEventsAsync, so it cannot run the DCB HasTag LINQ compliance suite.");

    /// <summary>
    /// Build the store's own <c>HasTag&lt;TTag&gt;(value)</c> marker predicate, for composing into
    /// the filter handed to <see cref="QueryRawEventsAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one part of a HasTag query a shared suite cannot write: both products translate the
    /// marker by matching the method's <em>declaring type</em> in their LINQ parsers, so the
    /// expression must invoke the product's own extension (Marten's
    /// <c>Marten.Events.LinqExtensions.HasTag</c>, Polecat's <c>Polecat.Linq</c> equivalent) — a
    /// lambda written here would carry the wrong <c>MethodInfo</c> and never be recognized. An
    /// implementation is one line: <c>e =&gt; e.HasTag(value)</c>.
    /// </para>
    /// <para>
    /// Building the expression must not itself validate the tag type: for an unregistered tag the
    /// products throw <see cref="InvalidOperationException"/> at query translation, which is the
    /// behavior the suite pins.
    /// </para>
    /// </remarks>
    public virtual Expression<Func<IEvent, bool>> HasTagFilter<TTag>(TTag value) where TTag : notnull
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement HasTagFilter, so it cannot run the DCB HasTag LINQ compliance suite.");

    /// <summary>
    /// Execute a batch data-masking operation against already-stored events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Protected.IEventDataMasking"/> itself is shared — it was lifted into
    /// <c>JasperFx.Events.Protected</c> in jasperfx#635 — but the entry point that hands one out is
    /// not. Both products spell it <c>Advanced.ApplyEventDataMasking(Action&lt;IEventDataMasking&gt;,
    /// CancellationToken)</c>, and both <c>Advanced</c> surfaces are store-specific types that share
    /// no interface, so the lift alone did not make masking reachable from a shared suite. This is
    /// the one member that closes that gap.
    /// </para>
    /// <para>
    /// Deliberately not typed as "give me the store's advanced operations" — that would drag an
    /// unbounded product surface into the seam. The suite asks for the one operation it needs.
    /// </para>
    /// </remarks>
    public abstract Task ApplyEventDataMaskingAsync(
        Action<Protected.IEventDataMasking> configure, CancellationToken token);

    /// <summary>
    /// Fold every event matched by the store's raw-event LINQ query into a single
    /// <typeparamref name="T"/> — the product's <c>AggregateToAsync&lt;T&gt;</c> operator over
    /// <c>QueryAllRawEvents()</c> — optionally seeded with <paramref name="initialState"/>.
    /// Must return null when the query matches no events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seam member because nothing shared can reach the operator: the LINQ-returning members are
    /// deliberately off <see cref="IQueryEventStore"/> (they return product-specific queryable
    /// types), and <c>AggregateToAsync</c> itself is an extension in each product's own namespace.
    /// The seam is deliberately the narrowest shape every fact needs — one optional predicate over
    /// the shared <see cref="IEvent"/>, applied in a single <c>Where()</c>, then the product's own
    /// terminator — for the same reason <see cref="QueryTableAsync"/> is predicate-free: the moment
    /// this grows ordering or paging it starts pinning one provider's operator set, which the
    /// library keeps out of scope permanently.
    /// </para>
    /// <para>
    /// Carries a throwing default because the operators are an opt-in capability rather than part of
    /// the baseline event contract; the affected suites gate on
    /// <see cref="SupportsAggregateToLinqOperators"/> and skip until a store implements the pair and
    /// flips the flag.
    /// </para>
    /// </remarks>
    public virtual Task<T?> AggregateEventsToAsync<T>(TQuerySession session,
        Expression<Func<IEvent, bool>>? filter, T? initialState, CancellationToken token) where T : class
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement AggregateEventsToAsync, so it cannot run the AggregateTo LINQ operator compliance suite.");

    /// <summary>
    /// Run the events matched by the store's raw-event LINQ query through the multi-stream
    /// projection registered for <typeparamref name="T"/> and return one aggregate per resulting
    /// identity — the product's <c>AggregateToManyAsync&lt;T&gt;</c> operator over
    /// <c>QueryAllRawEvents()</c>. Must throw <see cref="ArgumentException"/> when no registered
    /// projection produces <typeparamref name="T"/>, even for an empty result set.
    /// </summary>
    /// <inheritdoc cref="AggregateEventsToAsync{T}" path="/remarks"/>
    public virtual Task<IReadOnlyList<T>> AggregateEventsToManyAsync<T>(TQuerySession session,
        Expression<Func<IEvent, bool>>? filter, CancellationToken token) where T : class
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement AggregateEventsToManyAsync, so it cannot run the aggregate-to-many compliance suite.");

    /// <summary>
    /// False in stores that build live aggregators automatically and reject explicit registration.
    /// </summary>
    public virtual bool SupportsLiveAggregationRegistration => true;

    /// <summary>
    /// False where the store cannot slice one database by tenant.
    /// </summary>
    public virtual bool SupportsConjoinedEventTenancy => true;

    /// <summary>
    /// True in a store that has implemented the <c>IEvent.HasTag&lt;TTag&gt;</c> LINQ marker over
    /// its raw-event query and the <see cref="QueryRawEventsAsync"/> / <see cref="HasTagFilter{TTag}"/>
    /// seam members that reach it.
    /// </summary>
    /// <remarks>
    /// Defaults to false, unlike the older gates, because the seam members it guards carry throwing
    /// defaults: a store that enrolls <c>DcbHasTagLinqCompliance</c> before implementing the seam
    /// should skip cleanly rather than fail on the default. Implement the two members, flip this to
    /// true.
    /// </remarks>
    public virtual bool SupportsHasTagLinqPredicates => false;

    /// <summary>
    /// False where the store cannot run the async projection daemon in the test environment.
    /// </summary>
    public virtual bool SupportsAsyncDaemon => true;

    /// <summary>
    /// False in a store that has not implemented the event store explorer default-interface methods
    /// (<c>GetRecentStreamsAsync</c>, <c>GetStreamMetadataAsync</c>) at all.
    /// </summary>
    public virtual bool SupportsExplorerSurface => true;

    /// <summary>
    /// Can this fixture build a store backed by more than one database, from
    /// <see cref="ComplianceStoreConfig.TenantDatabases" />? Gates the multi-database explorer arms
    /// (jasperfx#810).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default <b>false</b>, unlike most gates here, because this one is about the <em>fixture</em>
    /// rather than the store: replaying a tenant-to-database map means creating and dropping real
    /// databases, and a fixture that has never had to do it keeps compiling and skipping rather than
    /// failing on a bump. Fisher is legitimately false — one file, one database.
    /// </para>
    /// <para>
    /// Saying true is a commitment to both halves: replay
    /// <see cref="ComplianceStoreConfig.TenantDatabases" />, and implement
    /// <see cref="DatabaseForTenantAsync" />. A fixture that says true and replays nothing does not
    /// skip — the isolation facts pass <em>vacuously</em> against a single-database store, which is
    /// the failure mode the arms exist to catch.
    /// </para>
    /// </remarks>
    public virtual bool SupportsMultipleDatabases => false;

    /// <summary>
    /// The <see cref="IEventDatabase" /> a tenant's data lives in, per
    /// <see cref="ComplianceStoreConfig.TenantDatabases" />. Needed because
    /// <see cref="IEventStore{TOperations,TQuerySession}.OpenSession(IEventDatabase,string)" /> takes
    /// a database, and nothing store-neutral resolves one from a tenant id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a fixture member and not a suite one. Every store spells this — Marten's
    /// <c>Tenancy.FindOrCreateDatabase(tenantId)</c> — but on its own tenancy object, and matching
    /// <see cref="IEventDatabase.Identifier" /> against a logical name from the config would only
    /// work for a fixture that happened to name its physical databases the same way.
    /// </para>
    /// <para>
    /// Throwing rather than returning the first of <see cref="IEventStore.AllDatabases" />: on a
    /// multi-database store the wrong database is not a degraded answer but a different one, and a
    /// fallback here would make the isolation facts assert about whichever database the store
    /// happened to hand back.
    /// </para>
    /// </remarks>
    public virtual ValueTask<IEventDatabase> DatabaseForTenantAsync(string tenantId)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement DatabaseForTenantAsync, so it cannot run the multi-database explorer compliance arms (jasperfx#810).");

    /// <summary>
    /// False in a store that has no flat-table event projection — an <c>EventProjection</c> writing
    /// into a plain relational table rather than a document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the other gates this one exists for a store that is still being built: a new consumer
    /// can enroll <c>FlatTableProjectionCompliance</c> from day one, leave this false, and flip it
    /// when the behavior lands, using the suite as the specification it is implementing against.
    /// Both current consumers leave it true.
    /// </para>
    /// <para>
    /// It gates <em>behavior</em>, not compilation. These suites compile inside the consumer, and
    /// the shared projection's constructor shim has to name a real flat-table base class, so a store
    /// still needs the type and its mapping API to exist before it can enroll at all. That ordering
    /// is deliberate: declare the surface, gate off, then make the assertions pass one at a time.
    /// </para>
    /// </remarks>
    public virtual bool SupportsFlatTableProjections => true;

    /// <summary>
    /// True in a store that has implemented the <c>AggregateToAsync</c> /
    /// <c>AggregateToManyAsync</c> operators over its raw-event LINQ query and the
    /// <see cref="AggregateEventsToAsync{T}"/> / <see cref="AggregateEventsToManyAsync{T}"/> seam
    /// members that reach them.
    /// </summary>
    /// <remarks>
    /// Defaults to false, unlike the older gates, because the seam members it guards carry throwing
    /// defaults: a store that enrolls
    /// <c>AggregateToManyCompliance</c> / <c>AggregateToLinqOperatorCompliance</c> before
    /// implementing the seam should skip cleanly rather than fail on the default. Implement the two
    /// members, flip this to true.
    /// </remarks>
    public virtual bool SupportsAggregateToLinqOperators => false;

    /// <summary>
    /// True in a store that has implemented the shared event upcasting contract
    /// (<c>JasperFx.Events.Upcasting</c>) — routing its read paths through
    /// <c>EventRegistry.Upcasters</c> and implementing <c>IUpcastPayload</c> over its own reader
    /// and serializer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to false, unlike the other gates, because the contract is being defined ahead of
    /// any store implementing it (jasperfx#752): a consumer that enrolls
    /// <c>UpcastingCompliance</c> today skips it wholesale and stays green, then flips this when
    /// the store's read path honors the registry. Like
    /// <see cref="SupportsFlatTableProjections" />, the suite is the specification a store
    /// implements against, and the gate is meant to be flipped rather than lived with.
    /// </para>
    /// <para>
    /// The gate also short-circuits configuration, not just facts: the suite never replays its
    /// upcast registrations through <see cref="IComplianceStoreRegistrar.Upcast" /> (which has a
    /// throwing default) while this is false.
    /// </para>
    /// </remarks>
    public virtual bool SupportsUpcasting => false;

    /// <summary>
    /// True in a store that maintains a natural key lookup for aggregates carrying a
    /// <c>[NaturalKey]</c> property, and resolves the shared
    /// <see cref="IEventStoreOperations.FetchForWriting{T,TId}" /> /
    /// <see cref="IEventStoreOperations.FetchForExclusiveWriting{T,TId}" /> /
    /// <see cref="IEventStoreOperations.FetchLatest{T,TId}" /> triple through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unusually for a capability gate, this one guards <em>no seam member at all</em>. The whole
    /// natural key surface is already shared: the <c>[NaturalKey]</c> / <c>[NaturalKeySource]</c>
    /// attributes and <c>NaturalKeyDefinition</c> live in <c>JasperFx.Events.Aggregation</c>,
    /// discovery runs in the shared <c>JasperFxAggregationProjectionBase</c>, the fetch triple is on
    /// <see cref="IEventStoreOperations"/>, and registration is the existing
    /// <see cref="ComplianceStoreConfig.Snapshot{TDoc}"/> plus
    /// <see cref="ComplianceStoreConfig.RegisterValueType{TValue}"/>. What varies is only whether a
    /// store has built the storage half — the lookup table and the read paths into it — which is
    /// exactly what <see cref="NaturalKeyCompliance{TFixture,TOperations,TQuerySession}"/> asserts.
    /// </para>
    /// <para>
    /// Defaults to false like the other recent gates, so a consumer can enroll the suite across the
    /// bump and flip it when the storage half lands. The gate short-circuits configuration as well
    /// as the facts, because a store with no natural key support may fail while <em>building</em> a
    /// store whose aggregates declare one.
    /// </para>
    /// </remarks>
    public virtual bool SupportsNaturalKeys => false;


    /// <summary>
    /// Build and START an application host whose container registers this store the way the
    /// product documents it — the product's own <c>AddXxx(...)</c> service registration plus its
    /// documented async daemon registration — replaying <paramref name="config"/> so the suite's
    /// projections are registered on the hosted store. Point the hosted store at the same database
    /// (and the config's schema) as the fixture's own store, so the per-test
    /// <see cref="CleanEventDataAsync"/> isolation covers it. Disposing the returned wrapper must
    /// stop the host.
    /// </summary>
    /// <param name="config">The suite's store-neutral configuration, to replay onto the hosted store.</param>
    /// <param name="includeAncillaryStore">
    /// True only when <see cref="SupportsAncillaryCoordinators"/> is true and the ancillary fact is
    /// running: additionally register one ancillary store (Marten's <c>AddMartenStore&lt;T&gt;</c>
    /// and its siblings) with its documented daemon registration, whose coordinator
    /// <see cref="AncillaryCoordinatorFrom"/> resolves. False for every other fact, and load-bearing
    /// there: the suite asserts the hosted-service walk finds EXACTLY one coordinator, so the
    /// default host must register only the primary store.
    /// </param>
    /// <remarks>
    /// <para>
    /// The seam jasperfx#732 names. Every other daemon suite drives a daemon the fixture built by
    /// hand (<see cref="StartDaemonAsync"/>), which can never observe whether the documented DI
    /// registration produces a reachable <see cref="IProjectionCoordinator"/> — fisher#138 shipped
    /// exactly that gap, and passed all 37 suites while it did.
    /// </para>
    /// <para>
    /// Carries a throwing default so consumers keep compiling across the bump, but unlike the
    /// opt-in capability members there is deliberately NO <c>Supports</c> flag and no skip. A store
    /// that enrolls <see cref="ProjectionCoordinatorCompliance{TFixture,TOperations,TQuerySession}"/>
    /// without implementing this member fails every fact rather than skipping, because a skippable
    /// registration check recreates the silent gap the suite exists to close — the third instance
    /// of the jasperfx#700 / jasperfx#718 pattern, and the reason the suite exists at all.
    /// </para>
    /// </remarks>
    protected virtual Task<IComplianceCoordinatorHost<TOperations>> StartCoordinatorHostAsync(
        ComplianceStoreConfig config, bool includeAncillaryStore)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement StartCoordinatorHostAsync, so it cannot run the projection coordinator compliance suite (jasperfx#732).");

    /// <summary>
    /// Start the coordinator host for the suite's current configuration — the one the last
    /// <see cref="ConfigureAsync"/> built.
    /// </summary>
    public Task<IComplianceCoordinatorHost<TOperations>> StartCoordinatorHostAsync(
        bool includeAncillaryStore = false)
    {
        if (CurrentConfig == null)
        {
            throw new InvalidOperationException(
                "ConfigureAsync must run before a coordinator host can be started.");
        }

        return StartCoordinatorHostAsync(CurrentConfig, includeAncillaryStore);
    }

    /// <summary>
    /// True in a store that supports ancillary store registration (Marten's
    /// <c>AddMartenStore&lt;T&gt;</c> and its siblings) whose hosted daemon registers a
    /// marker-typed <see cref="IProjectionCoordinator{T}"/>. Gates only the ancillary fact of
    /// <see cref="ProjectionCoordinatorCompliance{TFixture,TOperations,TQuerySession}"/> — the
    /// core coordinator facts deliberately have no gate.
    /// </summary>
    public virtual bool SupportsAncillaryCoordinators => false;

    /// <summary>
    /// Resolve the ancillary store's marker-typed coordinator from a host started with
    /// <c>includeAncillaryStore: true</c> — typically
    /// <c>services.GetRequiredService&lt;IProjectionCoordinator&lt;TMarker&gt;&gt;()</c> over the
    /// fixture's own marker type. A fixture member because the marker type cannot be shared: every
    /// product constrains ancillary markers to its own store interface, so only the fixture can
    /// name one.
    /// </summary>
    public virtual IProjectionCoordinator AncillaryCoordinatorFrom(IServiceProvider services)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement AncillaryCoordinatorFrom, so it cannot run the ancillary coordinator compliance fact.");

    /// <summary>
    /// True in a store that ships the two stream-fetch query plans — a <c>FetchStreamStatePlan</c>
    /// and a <c>FetchStreamPlan</c> usable both standalone and inside a batched query — and has
    /// implemented the two seam members that reach them.
    /// </summary>
    /// <remarks>
    /// Defaults to false: the plan types and the <c>QueryByPlan</c> entry points are per-product
    /// (each store declares its own <c>IQueryPlan&lt;T&gt;</c> / <c>IBatchQueryPlan&lt;T&gt;</c>), so
    /// a store without them enrolls
    /// <see cref="StreamQueryPlanCompliance{TFixture,TOperations,TQuerySession}"/> and skips.
    /// </remarks>
    public virtual bool SupportsStreamQueryPlans => false;

    /// <summary>
    /// Run the store's stream-state query plan for <paramref name="streamIdentity"/> — a
    /// <see cref="Guid"/> or a <see cref="string"/>, matching the store's stream identity.
    /// </summary>
    /// <param name="batched">
    /// When true the plan must be executed through the store's batched query surface rather than
    /// standalone. Load-bearing rather than convenience: the two paths compose their SQL separately
    /// on both products, so a plan that is correct standalone and wrong in a batch is the failure
    /// this suite is looking for.
    /// </param>
    /// <remarks>
    /// The result type is the shared <see cref="StreamState"/>, and the assertions are all about the
    /// stream — so only the *route* to the plan is per-product, which is why this is a forwarding
    /// member rather than an abstraction over query plans in general.
    /// </remarks>
    public virtual Task<StreamState?> FetchStreamStateByPlanAsync(
        TQuerySession session, object streamIdentity, bool batched, CancellationToken token)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement FetchStreamStateByPlanAsync, so it cannot run the stream query plan compliance suite.");

    /// <summary>
    /// Run the store's raw-event stream query plan for <paramref name="streamIdentity"/>.
    /// </summary>
    /// <param name="version">
    /// Inclusive version cap, or zero for no cap — the same meaning the shared
    /// <c>FetchStreamAsync</c> overloads give it.
    /// </param>
    /// <param name="batched">See <see cref="FetchStreamStateByPlanAsync"/>.</param>
    public virtual Task<IReadOnlyList<IEvent>> FetchStreamByPlanAsync(
        TQuerySession session, object streamIdentity, long version, bool batched, CancellationToken token)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement FetchStreamByPlanAsync, so it cannot run the stream query plan compliance suite.");

    /// <summary>
    /// True in a store that has implemented <c>UnArchiveStream</c> — reversing an archive so the
    /// stream's events are readable and appendable again.
    /// </summary>
    /// <remarks>
    /// Defaults to false because the operation is genuinely not universal: Polecat declares it on
    /// its own <c>IEventOperations</c> and Marten has no equivalent today, so it is not on the shared
    /// <see cref="IEventStoreOperations"/> and cannot be. The suite's unarchive facts skip rather
    /// than fail on a store without it; the archiving facts around them do not.
    /// </remarks>
    public virtual bool SupportsUnarchiveStream => false;

    /// <summary>
    /// Queue an unarchive of the given stream on the session — <paramref name="streamIdentity"/> is
    /// a <see cref="Guid"/> or a <see cref="string"/>. Like <c>ArchiveStream</c>, this takes effect
    /// on SaveChanges rather than immediately.
    /// </summary>
    public virtual void UnArchiveStream(TOperations session, object streamIdentity)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement UnArchiveStream, so it cannot run the unarchive compliance facts.");

    /// <summary>
    /// True in a store that has a message outbox — an <c>Events.MessageOutbox</c> a bus integration
    /// replaces — and routes a projection's published side effects through it. Gates the outbox
    /// facts of
    /// <see cref="ProjectionSideEffectCompliance{TFixture,TOperations,TQuerySession}"/>; the
    /// raised-event and rebuild-suppression facts need no outbox and are not gated on it.
    /// </summary>
    /// <remarks>
    /// Defaults to <strong>false</strong>, the same declare-the-surface-then-implement ordering as
    /// <see cref="SupportsUpcasting"/>: a store enrolls the suite, implements
    /// <see cref="IComplianceStoreRegistrar.UseMessageOutbox"/> and the two consumer partials on
    /// <see cref="RecordingMessageOutbox"/> / <see cref="RecordingMessageBatch"/>, then flips this.
    /// Gated rather than always-on because the outbox facts also drive store <em>construction</em>
    /// through the registrar member, so an ungated suite would reach its throwing default rather
    /// than skipping.
    /// </remarks>
    public virtual bool SupportsMessageOutbox => false;

    /// <summary>
    /// True where the outbox's commit hooks may safely read committed state over a second session
    /// while the first session's write transaction is still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gates the one fact that proves the commit <em>boundary</em> rather than merely the hook
    /// order — before-commit inside the transaction, after-commit outside it. It is separate from
    /// <see cref="SupportsMessageOutbox"/> because the risk is the engine's, not the outbox's: the
    /// before-commit probe reads a row the open transaction is in the middle of writing, which a
    /// snapshot/WAL reader answers immediately but a lock-based reader can block on — and a probe
    /// that blocks until the commit deadlocks against the hook that is holding the commit open.
    /// </para>
    /// <para>
    /// Default <strong>false</strong> for that reason: a store flips it once it knows its readers
    /// do not block behind an uncommitted write. Skipping costs one fact; guessing wrong hangs the
    /// suite.
    /// </para>
    /// </remarks>
    public virtual bool SupportsCommitVisibilityProbe => false;

    /// <summary>
    /// True in a store whose registrar replays <see cref="ComplianceSubscription.IncludedEventTypes"/>
    /// onto its own subscription registration, so a declared allow list actually reaches the daemon.
    /// Gates the one event-filter fact of
    /// <see cref="SubscriptionCompliance{TFixture,TOperations,TQuerySession}"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <strong>false</strong> because the filter has to survive a hop no shared code can
    /// make for it: every product's bare-<c>ISubscription</c> registration wraps the subscription in
    /// its own <c>SubscriptionBase</c>, and the daemon reads filters from the <em>wrapper</em>, which
    /// copies none across. Implementing it is one loop inside the registrar's <c>Subscribe</c> —
    /// replay each type onto the store's own <c>IncludeType</c> — and then this flips.
    /// </remarks>
    public virtual bool SupportsSubscriptionEventFilters => false;

    /// <summary>
    /// True in a store that subclasses the shared
    /// <see cref="TestSupport.ProjectionScenario{TOperations,TQuerySession}"/> harness, exposes a
    /// scenario entry point on its own advanced operations, and has implemented the two seam members
    /// below.
    /// </summary>
    /// <remarks>
    /// Defaults to false so a store can enroll
    /// <see cref="ProjectionScenarioCompliance{TFixture,TOperations,TQuerySession}"/> and skip until
    /// the harness is wired up.
    /// </remarks>
    public virtual bool SupportsProjectionScenario => false;

    /// <summary>
    /// Run a projection scenario through the store's <em>own documented entry point</em> — Marten's
    /// and Polecat's <c>Advanced.EventProjectionScenario(configure, token)</c>, Fisher's
    /// <c>Advanced.EventProjectionScenarioAsync</c> — forwarding <paramref name="configure"/> to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implement this as a forward, never as a re-implementation. All three products spell the entry
    /// point as construct-the-scenario, invoke the configuration, <c>ExecuteAsync</c>; a fixture that
    /// inlined those three lines would pass the whole suite while the store's advertised entry point
    /// was missing or wired to the wrong store. That route is the thing under test as much as the
    /// harness behind it.
    /// </para>
    /// <para>
    /// The parameter is typed to the shared base rather than the product's subclass, which needs no
    /// adaptation: the product's <c>Action&lt;TheirScenario&gt;</c> is satisfied by a lambda that
    /// hands its argument to this delegate.
    /// </para>
    /// </remarks>
    public virtual Task RunProjectionScenarioAsync(
        Action<TestSupport.ProjectionScenario<TOperations, TQuerySession>> configure, CancellationToken token)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement RunProjectionScenarioAsync, so it cannot run the projection scenario compliance suite.");

    /// <summary>
    /// Construct a scenario against the store without running it.
    /// </summary>
    /// <remarks>
    /// Exists for the one fact the run entry point structurally cannot reach: a scenario's steps are
    /// consumed by its first run, so proving a second run fails loudly rather than passing as a
    /// silent no-op needs a handle on the instance, and every product's entry point constructs one
    /// and throws it away. Both Marten's and Polecat's own tests reach for
    /// <c>new ProjectionScenario(theStore)</c> for exactly this.
    /// </remarks>
    public virtual TestSupport.ProjectionScenario<TOperations, TQuerySession> CreateProjectionScenario()
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement CreateProjectionScenario, so it cannot run the projection scenario compliance suite.");


    /// <summary>
    /// The exception type this store throws for a given failure category. Defaults to the matching
    /// exception lifted into <c>JasperFx.Events</c>, so a store that adopted the lifted types — by
    /// throwing them or by subclassing them — needs no override at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Virtual with a total default rather than abstract, deliberately: an abstract member would
    /// break every consuming store on the next package bump for the sake of a seam most of them do
    /// not need. Polecat and Fisher subclass the lifted types, so the default answers correctly for
    /// both.
    /// </para>
    /// <para>
    /// The seam exists because a store may legitimately own its exception hierarchy and therefore
    /// be unable to subclass a lifted type. Marten is the concrete case: its
    /// <c>all_exceptions_should_derive_from_MartenException</c> convention test requires every
    /// exception Marten throws to derive from <c>MartenException</c>, and C# has single
    /// inheritance, so a Marten type cannot derive from both that base and the lifted JasperFx
    /// type. Marten keeps its own six declarations in <c>Marten.Exceptions</c> and names them here.
    /// That is a store design decision the compliance library has no business overruling.
    /// </para>
    /// <para>
    /// Note what this does <strong>not</strong> loosen. The suites still demand an exception of the
    /// nominated type — the store picks the name, not whether the assertion has teeth. Nor does it
    /// touch the categories that are genuinely shared already: <c>JasperFx.ConcurrencyException</c>
    /// and <see cref="EventStreamUnexpectedMaxEventIdException"/> live in JasperFx, every store
    /// throws them directly, and the suites keep asserting those types outright.
    /// </para>
    /// <para>
    /// Resist the urge to "tidy" a <see cref="ComplianceExceptionKind"/> assertion back to a hard
    /// type. Doing so silently re-imposes a shared hierarchy on stores that have declined it, and
    /// the failure lands on the store rather than here.
    /// </para>
    /// </remarks>
    public virtual Type ExceptionTypeFor(ComplianceExceptionKind kind) =>
        kind switch
        {
            ComplianceExceptionKind.UnknownEventType => typeof(UnknownEventTypeException),
            ComplianceExceptionKind.NonExistentStream => typeof(NonExistentStreamException),
            ComplianceExceptionKind.ExistingStreamIdCollision => typeof(ExistingStreamIdCollisionException),
            ComplianceExceptionKind.EventDeserializationFailure => typeof(EventDeserializationFailureException),
            ComplianceExceptionKind.StreamLocked => typeof(StreamLockedException),
            ComplianceExceptionKind.DefaultTenantUsageDisabled => typeof(DefaultTenantUsageDisabledException),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                $"{GetType().FullName} was asked for an unknown compliance exception category.")
        };

    public virtual ValueTask InitializeAsync() => default;

    public virtual ValueTask DisposeAsync() => default;
}
