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
    /// False where the store cannot run the async projection daemon in the test environment.
    /// </summary>
    public virtual bool SupportsAsyncDaemon => true;

    /// <summary>
    /// False in a store that has not implemented the event store explorer default-interface methods
    /// (<c>GetRecentStreamsAsync</c>, <c>GetStreamMetadataAsync</c>) at all.
    /// </summary>
    public virtual bool SupportsExplorerSurface => true;

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


    public virtual ValueTask InitializeAsync() => default;

    public virtual ValueTask DisposeAsync() => default;
}
