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
        // Nothing to build: an in-memory store has no schema, and it creates storage for a document
        // type on first use. Both are legitimate answers to this seam member.
        _ = config;
        return Task.CompletedTask;
    }

    public override IDocumentSessionFactory Sessions => _store;

    public override Task CleanDocumentDataAsync()
    {
        _store.Clear();
        return Task.CompletedTask;
    }
}

public class in_memory_document_session_compliance
    : DocumentSessionCompliance<InMemoryDocumentComplianceFixture>;

public class in_memory_document_load_and_store_compliance
    : DocumentLoadAndStoreCompliance<InMemoryDocumentComplianceFixture>;

public class in_memory_document_delete_compliance
    : DocumentDeleteCompliance<InMemoryDocumentComplianceFixture>;

public class in_memory_document_query_compliance
    : DocumentQueryCompliance<InMemoryDocumentComplianceFixture>;
