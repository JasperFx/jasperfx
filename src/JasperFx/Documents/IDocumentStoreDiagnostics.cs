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
public record DocumentQueryOptions(int PageNumber, int PageSize, string? IdEquals = null)
{
    /// <summary>
    /// Optional tenant id to scope the query to. For conjoined tenancy the implementation filters by the
    /// store's tenant column; for database-per-tenant it targets that tenant's physical database. Null
    /// queries the default/main tenant. Added as an init-only member (not a positional parameter) so the
    /// record stays binary-compatible. See JasperFx/CritterWatch EVENT_STORE_EXPLORER_PLAN §3.1.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Optional exact-match filter on the document's correlation id metadata. Null applies no filter. Only
    /// honored when the store advertises and captures the correlation id metadata column; otherwise ignored.
    /// Added as an init-only member so the record stays binary-compatible. See JasperFx/CritterWatch #629.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional exact-match filter on the document's causation id metadata. Null applies no filter. Only
    /// honored when the store advertises and captures the causation id metadata column; otherwise ignored.
    /// Added as an init-only member so the record stays binary-compatible. See JasperFx/CritterWatch #629.
    /// </summary>
    public string? CausationId { get; init; }

    /// <summary>
    /// Optional exact-match filter on the document's "last modified by" user metadata. Null applies no
    /// filter. Only honored when the store advertises and captures the last-modified-by metadata column;
    /// otherwise ignored. Added as an init-only member so the record stays binary-compatible. See
    /// JasperFx/CritterWatch #629.
    /// </summary>
    public string? LastModifiedBy { get; init; }
}

/// <summary>A page of stored documents as raw JSON, with the total matching count for pager UIs.</summary>
public record DocumentQueryResult(IReadOnlyList<string> DocumentsJson, long TotalCount, int PageNumber, int PageSize);
