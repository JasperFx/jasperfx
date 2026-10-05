using JasperFx.Events;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;

namespace InMemoryEventStoreTests;

/// <summary>
/// The in-memory prototyping store's implementation of the cross-store event sourcing compliance seam
/// (jasperfx#964), closed over its <see cref="IInMemoryDocumentSession"/> / <see cref="IInMemoryQuerySession"/>
/// pair.
/// </summary>
/// <remarks>
/// The members behind what the store deliberately leaves out -- the async daemon, the explorer, batched
/// queries, flat tables, event masking -- throw rather than answer, and the matching <c>Supports*</c>
/// flags are off, so a suite that reaches one fails loudly instead of passing on a stub. xUnit builds a
/// fixture per test, so every test gets its own store.
/// </remarks>
public class InMemoryComplianceFixture : EventStoreComplianceFixture<IInMemoryDocumentSession, IInMemoryQuerySession>
{
    private InMemoryDocumentStore? _store;

    private InMemoryDocumentStore Store => _store
        ?? throw new InvalidOperationException("The compliance store has not been configured yet.");

    protected override Task BuildStoreAsync(ComplianceStoreConfig config)
    {
        if (config.ConjoinedEventTenancy)
        {
            throw new NotSupportedException("Events are single-tenanted on the in-memory prototyping store.");
        }

        var store = new InMemoryDocumentStore();

        if (config.StreamIdentity.HasValue)
        {
            store.Events.StreamIdentity = config.StreamIdentity.Value;
        }

        if (config.EnableCorrelationTracking)
        {
            store.Events.CorrelationIdEnabled = true;
            store.Events.CausationIdEnabled = true;
        }

        store.Events.UserNameEnabled = config.EnableUserNameTracking;
        store.Events.HeadersEnabled = config.EnableHeaders;

        // Not optional: a listener that was never registered never fires, which a fact asserting that
        // nothing was reported would pass vacuously.
        store.Listeners.AddRange(config.CommitListeners);

        config.ApplyTo(new InMemoryComplianceRegistrar(store));

        _store = store;
        return Task.CompletedTask;
    }

    public override IInMemoryDocumentSession OpenSession() => Store.LightweightSession();

    public override Task SaveChangesAsync(IInMemoryDocumentSession session, CancellationToken token)
        => session.SaveChangesAsync(token);

    public override Task<T?> LoadDocumentAsync<T>(IInMemoryQuerySession session, object id, CancellationToken token)
        where T : class
        => session.LoadAsync<T>(id, token);

    public override void StoreDocument<T>(IInMemoryDocumentSession session, T document) => session.Store(document);

    public override IEventStoreOperations EventsFor(IInMemoryDocumentSession session) => session.Events;

    public override string? CorrelationIdFor(IInMemoryDocumentSession session) => concrete(session).CorrelationId;

    public override string? CausationIdFor(IInMemoryDocumentSession session) => concrete(session).CausationId;

    public override void SetCorrelationId(IInMemoryDocumentSession session, string? correlationId)
        => concrete(session).CorrelationId = correlationId;

    public override void SetUserName(IInMemoryDocumentSession session, string? userName)
        => concrete(session).CurrentUserName = userName;

    private static InMemoryDocumentSession concrete(IInMemoryDocumentSession session) => (InMemoryDocumentSession)session;

    public override IEventRegistry Registry => Store.Events;

    public override IEnumerable<Type> AllAggregateTypes() => Store.Projections.AllAggregateTypes();

    public override Task CleanEventDataAsync()
    {
        // Called before every test, so it cannot throw the way the unsupported members below do
        _store?.Clear();
        return Task.CompletedTask;
    }

    // Inline projections are written in the commit, so nothing is ever stale
    public override Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => Task.CompletedTask;

    public override bool SupportsConjoinedEventTenancy => false;

    public override bool SupportsAsyncDaemon => false;

    public override bool SupportsExplorerSurface => false;

    public override bool SupportsFlatTableProjections => false;

    // ---- out of scope for a prototyping store ----

    public override IEventStore EventStore => throw notSupported(nameof(EventStore));

    public override IComplianceBatch CreateBatch(IInMemoryQuerySession session) => throw notSupported("Batched queries");

    public override Task<IProjectionDaemon> StartDaemonAsync() => throw notSupported("The async daemon");

    public override Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryTableAsync(string tableName,
        CancellationToken token)
        => throw notSupported("Flat table projections");

    public override Task ApplyEventDataMaskingAsync(Action<JasperFx.Events.Protected.IEventDataMasking> configure,
        CancellationToken token)
        => throw notSupported("Event data masking");

    private static NotSupportedException notSupported(string what)
        => new($"{what} is not supported by the in-memory prototyping store.");

    private sealed class InMemoryComplianceRegistrar(InMemoryDocumentStore store) : IComplianceStoreRegistrar
    {
        public void AddEventType(Type eventType) => store.Events.AddEventType(eventType);

        public ITagTypeRegistration RegisterTagType<TTag>(string tableSuffix) where TTag : notnull
            => throw notSupported("Tags (DCB)");

        public void Snapshot<TDoc>(SnapshotLifecycle lifecycle) where TDoc : notnull
            => store.Projections.Snapshot<TDoc>(lifecycle);

        // Live aggregators are also built on first use, so registering one only builds it early. Closed by
        // reflection because the store's method needs a class, which the seam does not promise.
        public void LiveAggregation<TDoc>() where TDoc : notnull
            => typeof(InMemoryProjectionGraph).GetMethod(nameof(InMemoryProjectionGraph.LiveStreamAggregation))!
                .MakeGenericMethod(typeof(TDoc))
                .Invoke(store.Projections, null);

        // Strong-typed identities are recognised by shape, with nothing to register
        public void RegisterValueType<TValue>() where TValue : notnull
        {
        }

        public void AddMaskingRule<TEvent>(Action<TEvent> rule) where TEvent : notnull
            => throw notSupported("Event data masking");

        public void AddMaskingRule<TEvent>(Func<TEvent, TEvent> rule) where TEvent : notnull
            => throw notSupported("Event data masking");

        public void AddProjection(ProjectionBase projection, ProjectionLifecycle lifecycle)
            => store.Projections.Add(projection, lifecycle);

        public void Subscribe(ComplianceSubscription subscription) => throw notSupported("Subscriptions");
    }
}
