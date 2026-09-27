using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Documents;

namespace EventStoreTests.Documents;

/// <summary>
/// Enrolls <see cref="InMemoryDocumentStore" /> in the shared document compliance suites.
/// </summary>
/// <remarks>
/// Three overrides, no generics — which is the point being demonstrated. Compare
/// <c>EventStoreComplianceFixture&lt;TOperations, TQuerySession&gt;</c>, whose sixteen abstract
/// members exist because so much of the event surface is only reachable through a product's own
/// session type.
/// </remarks>
public class InMemoryDocumentComplianceFixture : DocumentStorageComplianceFixture
{
    private readonly InMemoryDocumentStore _store = new();

    protected override Task BuildStoreAsync(DocumentComplianceConfig config)
    {
        // Nothing to build for storage: an in-memory store has no schema, and it creates storage for
        // a document type on first use. Both are legitimate answers to this seam member.

        // Commit listeners are the exception, and the reason this override is no longer empty.
        // Registration happens when the store is built, before any session exists, so a fixture that
        // skipped this would fail every fact in DocumentCommitListenerCompliance rather than
        // skipping them -- a listener that was never registered is indistinguishable from a store
        // that never invokes one. Every product spells the same replay against its own
        // StoreOptions.Listeners.
        _store.Listeners.Clear();
        _store.Listeners.AddRange(config.CommitListeners);

        // Conjoined document tenancy (jasperfx#898), replayed the same way: a per-type declaration
        // that decides which bucket a row lands in. The products spell it
        // Schema.For<T>().MultiTenanted(); here it is a set the storage lookup consults. Dropping
        // this replay would not make DocumentConjoinedTenancyCompliance skip -- its isolation facts
        // would FAIL, because a single-tenanted store folds both tenants' writes into one row.
        _store.ConjoinedTypes.Clear();
        foreach (var type in config.ConjoinedDocuments)
        {
            _store.ConjoinedTypes.Add(type);
        }

        return Task.CompletedTask;
    }

    public override IDocumentSessionFactory Sessions => _store;

    public override Task CleanDocumentDataAsync()
    {
        _store.Clear();
        return Task.CompletedTask;
    }

    public override bool SupportsConjoinedDocuments => true;

    public override bool SupportsCrossTenantQueries => true;

    /// <inheritdoc />
    /// <remarks>
    /// The session argument is ignored, and legitimately so: the escape's whole meaning is that it
    /// steps outside the session's tenant scope. A real store still routes through the session,
    /// because that is where its connection and its query provider live.
    /// </remarks>
    public override Task<IReadOnlyList<T>> QueryAllTenantsAsync<T>(
        IDocumentReadOperations session, CancellationToken token)
        => Task.FromResult(_store.SnapshotAcrossTenants<T>());

    /// <inheritdoc cref="QueryAllTenantsAsync{T}" />
    public override Task<IReadOnlyList<T>> QueryTenantsAsync<T>(
        IDocumentReadOperations session, string[] tenantIds, CancellationToken token)
        => Task.FromResult(_store.SnapshotForTenants<T>(tenantIds));
}

public class in_memory_document_session_compliance
    : DocumentSessionCompliance<InMemoryDocumentComplianceFixture>;

public class in_memory_document_load_and_store_compliance
    : DocumentLoadAndStoreCompliance<InMemoryDocumentComplianceFixture>;

public class in_memory_document_delete_compliance
    : DocumentDeleteCompliance<InMemoryDocumentComplianceFixture>;

public class in_memory_document_query_compliance
    : DocumentQueryCompliance<InMemoryDocumentComplianceFixture>;

public class in_memory_document_commit_listener_compliance
    : DocumentCommitListenerCompliance<InMemoryDocumentComplianceFixture>;

/// <summary>
/// Enrolled so the search suite is COMPILED against a real fixture, not so it runs here.
/// </summary>
/// <remarks>
/// <see cref="InMemoryDocumentComplianceFixture.SupportsVectorSearch" /> is the inherited
/// <c>false</c>, so every fact skips: an in-memory store has no vector index and no full-text index,
/// and faking one would assert the fake. What the enrollment buys is that the suite cannot rot —
/// a signature change in <see cref="JasperFx.Events.Vectors.IDocumentSearchOperations" /> breaks this
/// build rather than three stores' builds a release later.
/// </remarks>
public class in_memory_document_search_compliance
    : DocumentSearchCompliance<InMemoryDocumentComplianceFixture>;

/// <summary>
/// Conjoined document tenancy (jasperfx#898), enrolled so the suite is <em>run</em> here rather than
/// shipping unexecuted to three stores.
/// </summary>
/// <remarks>
/// Seven of its nine facts run. The optimistic-concurrency and numeric-revision facts skip, because
/// this store implements neither mechanism and inventing one to exercise the tenant axis over would
/// be asserting the fake — the same call <see cref="in_memory_document_search_compliance" /> makes
/// about vector indexes. What the seven buy is the thing worth buying: the shared source is known to
/// compile and to be satisfiable before Marten, Polecat and Fisher are held to it.
/// </remarks>
public class in_memory_document_conjoined_tenancy_compliance
    : DocumentConjoinedTenancyCompliance<InMemoryDocumentComplianceFixture>;
