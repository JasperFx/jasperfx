using JasperFx.Documents;
using Shouldly;

namespace CoreTests.Documents;

/// <summary>
/// The default <see cref="IDocumentStoreDiagnosticsWriter.UndeleteDocumentAsync" /> that lets a store built
/// against JasperFx 2.92 or older load beside a newer JasperFx (jasperfx#1059, jasperfx#933).
/// </summary>
/// <remarks>
/// <see cref="Pre293Writer" /> implements exactly the members the contract had in 2.92; that it compiles is
/// half the test.
/// </remarks>
public class DocumentStoreDiagnosticsWriterDefaultsTests
{
    [Fact]
    public async Task a_pre_2_93_store_refuses_undelete_naming_itself()
    {
        IDocumentStoreDiagnosticsWriter writer = new Pre293Writer();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            writer.UndeleteDocumentAsync(new DocumentUndeleteRequest("Widget", "1")));

        ex.Message.ShouldContain(typeof(Pre293Writer).FullName!);
        ex.Message.ShouldContain("2.93");
    }

    /// <summary>The writer contract exactly as it stood in JasperFx 2.92.</summary>
    private class Pre293Writer : IDocumentStoreDiagnosticsWriter
    {
        public Uri Subject => new("store://pre293");

        public Task<DocumentWriteResult> SaveDocumentJsonAsync(DocumentWriteRequest request,
            CancellationToken token = default)
            => Task.FromResult(new DocumentWriteResult(DocumentWriteStatus.Saved));

        public Task<DocumentWriteResult> DeleteDocumentAsync(DocumentDeleteRequest request,
            CancellationToken token = default)
            => Task.FromResult(new DocumentWriteResult(DocumentWriteStatus.Deleted));
    }
}
