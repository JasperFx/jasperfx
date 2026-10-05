using System.Reflection;
using JasperFx.Documents;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;

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

        // Guid optimistic concurrency (jasperfx#819), replayed for the reason jasperfx#903 made
        // concrete: while this gate was false, the tenancy suite's concurrency fact SKIPPED here and
        // shipped asserting something no correct store could satisfy. Every product spells this
        // Schema.For<T>().UseOptimisticConcurrency(true).
        _store.OptimisticConcurrencyTypes.Clear();
        foreach (var type in config.OptimisticConcurrencyTypes)
        {
            _store.OptimisticConcurrencyTypes.Add(type);
        }

        // polecat#720 — the same guard reached through a member the configuration names. The products
        // spell this Schema.For<T>().Metadata(m => m.Version.MapTo(x => x.Member)); here the member
        // name resolves straight to its MemberInfo, which is all the store needs.
        _store.MappedVersionMembers.Clear();
        foreach (var declaration in config.MappedVersionMembers)
        {
            var member = (MemberInfo?)declaration.DocumentType.GetProperty(declaration.MemberName)
                         ?? declaration.DocumentType.GetField(declaration.MemberName);
            if (member is null)
            {
                throw new InvalidOperationException(
                    $"Document type '{declaration.DocumentType.FullName}' has no member named " +
                    $"'{declaration.MemberName}' to map the version column onto.");
            }

            _store.MappedVersionMembers[declaration.DocumentType] = member;
        }

        // Document types, soft deletes and hierarchies (jasperfx#870): what the diagnostics surface is
        // defined over. The products spell these Schema.For<T>().SoftDeleted() and
        // Schema.For<TRoot>().AddSubClass<TSub>().
        _store.DocumentTypes.Clear();
        _store.DocumentTypes.UnionWith(config.DocumentTypes);

        _store.SoftDeletedTypes.Clear();
        _store.SoftDeletedTypes.UnionWith(config.SoftDeletedDocuments);

        _store.SubClassRoots.Clear();
        foreach (var declaration in config.SubClasses)
        {
            _store.SubClassRoots[declaration.SubClass] = declaration.Root;
        }

        return Task.CompletedTask;
    }

    public override IDocumentSessionFactory Sessions => _store;

    public override bool SupportsDocumentDiagnostics => true;

    public override IDocumentStoreDiagnostics DocumentDiagnostics => _store;

    public override bool SupportsDocumentDiagnosticWrites => true;

    public override IDocumentStoreDiagnosticsWriter DocumentDiagnosticsWriter => _store;

    public override bool SupportsSoftDeletedDocuments => true;

    public override bool SupportsDocumentHierarchies => true;

    public override Task CleanDocumentDataAsync()
    {
        _store.Clear();
        return Task.CompletedTask;
    }

    public override bool SupportsConjoinedDocuments => true;

    public override bool SupportsCrossTenantQueries => true;

    public override bool SupportsDocumentDiagnosticAllTenants => true;

    /// <summary>
    /// True since jasperfx#903. See <see cref="in_memory_guid_optimistic_concurrency_compliance" /> for
    /// why this is worth implementing in a test double.
    /// </summary>
    public override bool SupportsOptimisticConcurrency => true;

    /// <summary>
    /// True from the suite's first release, deliberately. polecat#720 is the fourth independent
    /// sighting of this field, and two of those came <em>after</em> a shared suite existed for the
    /// marker-interface half — so landing the mapped facts in the state that produced them (gated
    /// false everywhere, never executed) would repeat jasperfx#903 knowingly.
    /// </summary>
    public override bool SupportsMappedConcurrencyMember => true;

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

/// <summary>
/// Guid optimistic concurrency, enrolled because of jasperfx#903.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the search suite, this one is enrolled to <em>run</em>, and implementing the guard in a test
/// double is a deliberate departure from "faking one would assert the fake". The guard is not a storage
/// engine feature here — it is a version comparison and a write-back, both fully specified by the five
/// facts of this suite — so there is nothing to fake.
/// </para>
/// <para>
/// What it buys is the thing jasperfx#903 cost. While <c>SupportsOptimisticConcurrency</c> was false,
/// <c>DocumentConjoinedTenancyCompliance</c>'s concurrency fact skipped here and reached three real
/// stores asserting a refusal that no store honouring
/// <see cref="a_successful_write_moves_the_instances_own_version_on" /> can produce. That
/// contradiction is between a fact in THIS suite and a fact in that one, so only a store enrolled in
/// both can catch it — which is now this one, before the next wave ships.
/// </para>
/// </remarks>
public class in_memory_guid_optimistic_concurrency_compliance
    : GuidOptimisticConcurrencyCompliance<InMemoryDocumentComplianceFixture>;

/// <summary>
/// The document diagnostics surface and its write-side sibling (jasperfx#870), enrolled to <em>run</em>.
/// </summary>
/// <remarks>
/// Everything but the criteria facts runs. <c>SupportsDocumentDiagnosticCriteria</c> stays false because
/// Dynamic LINQ is jasperfx#869, not this double's to fake — so the refusal fact runs instead, which is
/// the half of §1 a store without predicate support is held to.
/// </remarks>
public class in_memory_document_store_diagnostics_compliance
    : DocumentStoreDiagnosticsCompliance<InMemoryDocumentComplianceFixture>;
