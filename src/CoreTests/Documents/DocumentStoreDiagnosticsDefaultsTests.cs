using JasperFx.Documents;
using Shouldly;

namespace CoreTests.Documents;

/// <summary>
/// The default implementations on <see cref="IDocumentStoreDiagnostics" /> that let a store built
/// against JasperFx 2.76 or older load beside a newer JasperFx (jasperfx#931).
/// </summary>
/// <remarks>
/// <see cref="Pre277Store" /> implements exactly the members the contract had in 2.76. That it compiles
/// at all is half the test: a new abstract member would break it here, at build time, rather than in an
/// application at startup with a <see cref="TypeLoadException" />.
/// </remarks>
public class DocumentStoreDiagnosticsDefaultsTests
{
    [Fact]
    public async Task a_pre_2_77_store_loads_from_the_default_tenant_through_its_json_load()
    {
        IDocumentStoreDiagnostics store = new Pre277Store();

        var document = await store.LoadDocumentAsync("Widget", "1", null);

        document.ShouldNotBeNull();
        document.Id.ShouldBe("1");
        document.Json.ShouldBe("""{"id":"1"}""");
        document.DocumentType.ShouldBe("Widget");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task a_blank_tenant_is_the_default_tenant_on_a_pre_2_77_store(string tenantId)
    {
        IDocumentStoreDiagnostics store = new Pre277Store();

        (await store.LoadDocumentAsync("Widget", "1", tenantId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_pre_2_77_store_still_returns_null_for_a_miss()
    {
        IDocumentStoreDiagnostics store = new Pre277Store();

        (await store.LoadDocumentAsync("Widget", "missing", null)).ShouldBeNull();
    }

    [Fact]
    public async Task a_pre_2_77_store_refuses_a_named_tenant_rather_than_reading_the_default_one()
    {
        IDocumentStoreDiagnostics store = new Pre277Store();

        var ex = await Should.ThrowAsync<NotSupportedException>(() => store.LoadDocumentAsync("Widget", "1", "acme"));

        ex.Message.ShouldContain(typeof(Pre277Store).FullName!);
        ex.Message.ShouldContain("acme");
    }

    [Fact]
    public void a_pre_2_77_store_refuses_subject_naming_itself()
    {
        IDocumentStoreDiagnostics store = new Pre277Store();

        var ex = Should.Throw<NotSupportedException>(() => store.Subject);

        ex.Message.ShouldContain(typeof(Pre277Store).FullName!);
        ex.Message.ShouldContain("2.77");
    }

    [Fact]
    public async Task a_store_implementing_neither_load_throws_rather_than_recursing()
    {
        IDocumentStoreDiagnostics store = new NoLoadStore();

        await Should.ThrowAsync<NotSupportedException>(() => store.LoadDocumentAsync("Widget", "1", null));
        await Should.ThrowAsync<NotSupportedException>(() => store.LoadDocumentJsonAsync("Widget", "1"));
    }

    [Fact]
    public async Task a_current_store_serves_the_json_load_from_its_own_load()
    {
        IDocumentStoreDiagnostics store = new CurrentStore();

        (await store.LoadDocumentJsonAsync("Widget", "1")).ShouldBe("""{"id":"1","tenant":null}""");
        (await store.LoadDocumentJsonAsync("Widget", "missing")).ShouldBeNull();
    }

    /// <summary>The contract exactly as it stood in JasperFx 2.76.</summary>
    private class Pre277Store : IDocumentStoreDiagnostics
    {
        public Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<DocumentTypeRef>>([]);

        public Task<DocumentQueryResult> QueryDocumentsAsync(string documentTypeName, DocumentQueryOptions options,
            CancellationToken token = default)
            => Task.FromResult(new DocumentQueryResult(Array.Empty<string>(), 0, 1, 10));

        public Task<string?> LoadDocumentJsonAsync(string documentTypeName, string id,
            CancellationToken token = default)
            => Task.FromResult(id == "missing" ? null : $$"""{"id":"{{id}}"}""");
    }

    private class NoLoadStore : IDocumentStoreDiagnostics
    {
        public Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<DocumentTypeRef>>([]);

        public Task<DocumentQueryResult> QueryDocumentsAsync(string documentTypeName, DocumentQueryOptions options,
            CancellationToken token = default)
            => Task.FromResult(new DocumentQueryResult(Array.Empty<string>(), 0, 1, 10));
    }

    private class CurrentStore : IDocumentStoreDiagnostics
    {
        public Uri Subject => new("store://current");

        public Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<DocumentTypeRef>>([]);

        public Task<DocumentQueryResult> QueryDocumentsAsync(string documentTypeName, DocumentQueryOptions options,
            CancellationToken token = default)
            => Task.FromResult(new DocumentQueryResult(Array.Empty<string>(), 0, 1, 10));

        public Task<StoredDocument?> LoadDocumentAsync(string documentTypeName, string id, string? tenantId,
            CancellationToken token = default)
            => Task.FromResult(id == "missing"
                ? null
                : new StoredDocument(id, $$"""{"id":"{{id}}","tenant":{{(tenantId is null ? "null" : $"\"{tenantId}\"")}}}"""));
    }
}
