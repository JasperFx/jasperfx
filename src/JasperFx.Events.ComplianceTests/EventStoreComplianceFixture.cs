using System;
using System.Collections.Generic;
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
    /// False in stores that build live aggregators automatically and reject explicit registration.
    /// </summary>
    public virtual bool SupportsLiveAggregationRegistration => true;

    /// <summary>
    /// False where the store cannot run the async projection daemon in the test environment.
    /// </summary>
    public virtual bool SupportsAsyncDaemon => true;

    public virtual ValueTask InitializeAsync() => default;

    public virtual ValueTask DisposeAsync() => default;
}
