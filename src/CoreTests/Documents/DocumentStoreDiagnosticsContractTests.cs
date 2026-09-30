using JasperFx.Documents;
using Shouldly;

namespace CoreTests.Documents;

/// <summary>
/// The store-independent half of jasperfx#870: the parts of the diagnostics contract that are defined
/// in JasperFx itself rather than by each store. Store behavior is pinned by
/// <c>DocumentStoreDiagnosticsCompliance</c>.
/// </summary>
public class DocumentStoreDiagnosticsContractTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("acme", "acme")]
    public void normalize_tenant_id(string? tenantId, string? expected)
    {
        // CritterWatch#1304: an empty tenant was read as a tenant *named* "".
        DocumentQueryOptions.NormalizeTenantId(tenantId).ShouldBe(expected);
    }

    [Fact]
    public void new_criteria_default_to_off()
    {
        var options = new DocumentQueryOptions(1, 20);

        options.Where.ShouldBeNull();
        options.OrderBy.ShouldBeNull();
        options.Arguments.ShouldBeNull();
        options.IncludeSoftDeleted.ShouldBeFalse();
    }

    [Fact]
    public void result_built_from_stored_documents_carries_both_shapes_in_order()
    {
        var documents = new[]
        {
            new StoredDocument("1", """{"id":1}""") { Version = "7" },
            new StoredDocument("2", """{"id":2}""") { Version = "8" }
        };

        var result = new DocumentQueryResult(documents, 10, 2, 2);

        result.DocumentsJson.ShouldBe(["""{"id":1}""", """{"id":2}"""]);
        result.Documents.ShouldBe(documents);
        result.TotalCount.ShouldBe(10);
        result.PageNumber.ShouldBe(2);
        result.PageSize.ShouldBe(2);
    }

    [Fact]
    public void result_built_the_old_way_has_no_metadata_rows()
    {
        // An implementation predating #870 — a consumer falls back to DocumentsJson.
        new DocumentQueryResult(["{}"], 1, 1, 1).Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task legacy_json_load_forwards_to_the_default_tenant()
    {
        IDocumentStoreDiagnostics diagnostics = new RecordingDiagnostics();

        var json = await diagnostics.LoadDocumentJsonAsync("Order", "42");

        json.ShouldBe("""{"id":"42"}""");
        ((RecordingDiagnostics)diagnostics).LastTenant.ShouldBeNull();
    }

    [Fact]
    public async Task legacy_json_load_returns_null_when_not_found()
    {
        IDocumentStoreDiagnostics diagnostics = new RecordingDiagnostics();

        (await diagnostics.LoadDocumentJsonAsync("Order", "missing")).ShouldBeNull();
    }

    [Fact]
    public void refusal_names_the_criterion()
    {
        var ex = new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.Where), "no predicate support");

        ex.ShouldBeAssignableTo<NotSupportedException>();
        ex.Criterion.ShouldBe("Where");
        ex.Message.ShouldContain("Where");
        ex.Message.ShouldContain("no predicate support");
    }

    private class RecordingDiagnostics : IDocumentStoreDiagnostics
    {
        public string? LastTenant { get; private set; } = "unset";

        public Uri Subject { get; } = new("fake://main");

        public Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<DocumentTypeRef>>([]);

        public Task<DocumentQueryResult> QueryDocumentsAsync(string documentTypeName, DocumentQueryOptions options,
            CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<StoredDocument?> LoadDocumentAsync(string documentTypeName, string id, string? tenantId,
            CancellationToken token = default)
        {
            LastTenant = tenantId;
            return Task.FromResult(id == "missing" ? null : new StoredDocument(id, $$"""{"id":"{{id}}"}"""));
        }
    }
}
