namespace JasperFx.Documents;

/// <summary>
/// Store-agnostic, read-only query surface over a document store's stored documents (#544 / #545,
/// JasperFx/CritterWatch). Mirrors the role <c>JasperFx.Events.IEventStore</c> plays for event streams:
/// it lets a monitoring console (which must not reference Marten / Polecat directly) browse, page, and
/// fetch stored documents as JSON regardless of the backing store. Implemented by Marten and Polecat
/// and registered in DI; consumers use the graceful-no-op pattern
/// (<c>services.GetServices&lt;IDocumentStoreDiagnostics&gt;()</c> is empty on a store that predates this,
/// so the feature degrades to "not available").
/// </summary>
public interface IDocumentStoreDiagnostics
{
    /// <summary>
    /// The mapped document types this store can query (CLR type name + table alias + schema), so a
    /// console can populate a type picker without a separate metadata round-trip.
    /// </summary>
    Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token = default);

    /// <summary>
    /// A page of documents of the named type, each as raw JSON, plus the total matching count.
    /// </summary>
    Task<DocumentQueryResult> QueryDocumentsAsync(
        string documentTypeName, DocumentQueryOptions options, CancellationToken token = default);

    /// <summary>
    /// One document of the named type by its string id, as raw JSON, or <see langword="null"/> when
    /// not found.
    /// </summary>
    Task<string?> LoadDocumentJsonAsync(
        string documentTypeName, string id, CancellationToken token = default);
}

/// <summary>A queryable document type on a store: CLR type name, table alias, and schema.</summary>
public record DocumentTypeRef(string TypeName, string Alias, string SchemaName);

/// <summary>
/// Options for <see cref="IDocumentStoreDiagnostics.QueryDocumentsAsync"/>. Paging is required; the
/// optional <see cref="IdEquals"/> narrows to a single id. Room to grow simple criteria later without a
/// signature break.
/// </summary>
public record DocumentQueryOptions(int PageNumber, int PageSize, string? IdEquals = null);

/// <summary>A page of stored documents as raw JSON, with the total matching count for pager UIs.</summary>
public record DocumentQueryResult(IReadOnlyList<string> DocumentsJson, long TotalCount, int PageNumber, int PageSize);
