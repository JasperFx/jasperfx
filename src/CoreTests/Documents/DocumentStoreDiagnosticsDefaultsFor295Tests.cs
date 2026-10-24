using JasperFx.Descriptors;
using JasperFx.Documents;
using Shouldly;

namespace CoreTests.Documents;

/// <summary>
/// The JasperFx 2.95 additions to the document diagnostics contract, as a store built against 2.94 sees them
/// (jasperfx#1077, #1078, #1081, #1082, and the jasperfx#933 rule that a new member never breaks loading).
/// </summary>
public class DocumentStoreDiagnosticsDefaultsFor295Tests
{
    [Fact]
    public async Task a_pre_2_95_store_cannot_explain_and_says_so_with_null()
    {
        IDocumentStoreDiagnostics diagnostics = new Pre295Diagnostics();

        (await diagnostics.ExplainDocumentQueryAsync("Widget", new DocumentQueryOptions(1, 10))).ShouldBeNull();
    }

    [Fact]
    public async Task a_pre_2_95_store_refuses_to_aggregate_naming_itself()
    {
        IDocumentStoreDiagnostics diagnostics = new Pre295Diagnostics();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            diagnostics.AggregateDocumentJsonAsync("Widget", "1", null));

        ex.Message.ShouldContain(typeof(Pre295Diagnostics).FullName!);
        ex.Message.ShouldContain("2.95");
    }

    [Fact]
    public void a_type_ref_from_an_older_store_names_no_single_stream_projection()
        => new DocumentTypeRef("Widget", "widget", "public").SingleStreamProjection.ShouldBeNull();

    [Fact]
    public void a_descriptor_from_an_older_store_reports_nothing()
    {
        var descriptor = new DocumentMappingDescriptor();

        descriptor.IndexesReported.ShouldBeFalse();
        descriptor.DuplicatedFieldsReported.ShouldBeFalse();
        descriptor.Metadata.ShouldBeNull();
    }

    [Fact]
    public void no_metadata_filter_is_nothing_to_refuse()
        => new DocumentQueryOptions(1, 10).AssertMetadataFiltersAreCaptured("Widget", false, false, false);

    [Fact]
    public void a_captured_metadata_filter_is_not_refused()
        => new DocumentQueryOptions(1, 10) { CorrelationId = "c", CausationId = "d", LastModifiedBy = "e" }
            .AssertMetadataFiltersAreCaptured("Widget", true, true, true);

    [Theory]
    [InlineData(nameof(DocumentQueryOptions.CorrelationId))]
    [InlineData(nameof(DocumentQueryOptions.CausationId))]
    [InlineData(nameof(DocumentQueryOptions.LastModifiedBy))]
    public void an_uncaptured_metadata_filter_is_refused_naming_the_criterion_and_the_type(string criterion)
    {
        var options = criterion switch
        {
            nameof(DocumentQueryOptions.CorrelationId) => new DocumentQueryOptions(1, 10) { CorrelationId = "c" },
            nameof(DocumentQueryOptions.CausationId) => new DocumentQueryOptions(1, 10) { CausationId = "c" },
            _ => new DocumentQueryOptions(1, 10) { LastModifiedBy = "c" }
        };

        // Each column is captured except the one being filtered on, so the refusal has to be per column.
        var ex = Should.Throw<DocumentCriteriaNotSupportedException>(() => options.AssertMetadataFiltersAreCaptured(
            "Shop.Widget",
            criterion != nameof(DocumentQueryOptions.CorrelationId),
            criterion != nameof(DocumentQueryOptions.CausationId),
            criterion != nameof(DocumentQueryOptions.LastModifiedBy)));

        ex.Criterion.ShouldBe(criterion);
        ex.Message.ShouldContain("Shop.Widget");
    }

    /// <summary>The read contract's required members, as a store built against 2.94 implements them.</summary>
    private class Pre295Diagnostics : IDocumentStoreDiagnostics
    {
        public Uri Subject => new("store://pre295");

        public Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<DocumentTypeRef>>([]);

        public Task<DocumentQueryResult> QueryDocumentsAsync(string documentTypeName, DocumentQueryOptions options,
            CancellationToken token = default)
            => Task.FromResult(new DocumentQueryResult((IReadOnlyList<StoredDocument>)[], 0, 1, 10));

        public Task<StoredDocument?> LoadDocumentAsync(string documentTypeName, string id, string? tenantId,
            CancellationToken token = default)
            => Task.FromResult<StoredDocument?>(null);
    }
}
