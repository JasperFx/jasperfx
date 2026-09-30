namespace JasperFx.Documents;

/// <summary>
/// Store-agnostic, read-only query surface over a document store's stored documents (#544 / #545,
/// JasperFx/CritterWatch). Mirrors the role <c>JasperFx.Events.IEventStore</c> plays for event streams:
/// it lets a monitoring console (which must not reference Marten / Polecat directly) browse, page, and
/// fetch stored documents as JSON regardless of the backing store. Implemented by Marten, Polecat and
/// Fisher and registered in DI; consumers use the graceful-no-op pattern
/// (<c>services.GetServices&lt;IDocumentStoreDiagnostics&gt;()</c> is empty on a store that predates this,
/// so the feature degrades to "not available").
/// </summary>
/// <remarks>
/// <para>
/// <b>Defined semantics (jasperfx#870).</b> The three implementations used to disagree on every one of
/// these, so they are now part of the contract and pinned by <c>DocumentStoreDiagnosticsCompliance</c>:
/// </para>
/// <list type="bullet">
/// <item><description><b>Soft deletes.</b> <see cref="QueryDocumentsAsync"/> excludes soft-deleted rows
/// unless <see cref="DocumentQueryOptions.IncludeSoftDeleted"/> is set. A load by id
/// (<see cref="LoadDocumentAsync"/>) is explicit, so it returns a soft-deleted row flagged with
/// <see cref="StoredDocument.IsDeleted"/> rather than hiding it.</description></item>
/// <item><description><b>Hierarchies.</b> The result is filtered to the requested type: naming a
/// sub-class returns only that sub-class's rows, naming the root returns every row in the table
/// (each of them <em>is</em> a root). <see cref="StoredDocument.DocumentType"/> says which.</description></item>
/// <item><description><b>Tenancy.</b> A null, empty or whitespace tenant id means the default tenant —
/// never "all tenants", and never a tenant literally named <c>""</c>. See
/// <see cref="DocumentQueryOptions.NormalizeTenantId"/>. For database-per-tenant the implementation
/// targets that tenant's physical database.</description></item>
/// <item><description><b>Identity.</b> An id arrives as text and is converted to the mapping's stored
/// identity type before it is compared, so the comparison can use the primary-key index and so an
/// upper-case Guid still matches.</description></item>
/// <item><description><b>Unknown type names</b> read as empty — an empty page, a null load — rather than
/// failing.</description></item>
/// </list>
/// <para>
/// <b>Type names.</b> <see cref="DocumentTypeRef.TypeName"/> is the type's
/// <c>FullNameInCode()</c>, and every member taking a <c>documentTypeName</c> accepts that spelling for
/// both mapped roots and registered sub-classes. Stores may additionally accept the short name or alias.
/// </para>
/// </remarks>
public interface IDocumentStoreDiagnostics
{
    /// <summary>
    /// Stable URI identifying the store this surface reads (jasperfx#870 §2). Marten and Polecat register
    /// one <see cref="IDocumentStoreDiagnostics"/> for the main store and another per ancillary store, so
    /// <c>GetService&lt;IDocumentStoreDiagnostics&gt;()</c> alone cannot target a particular one.
    /// </summary>
    /// <remarks>
    /// Must equal the same store's <c>JasperFx.Events.IDocumentStoreUsageSource.Subject</c> — the value a
    /// console already reads off <c>DocumentStoreUsage.SubjectUri</c> — so the two can be paired without
    /// a lookup table. On every current store the <c>DocumentStore</c> implements both interfaces, and a
    /// single <c>Subject</c> property satisfies them together.
    /// </remarks>
    Uri Subject { get; }

    /// <summary>
    /// The mapped document types this store can query (CLR type name + table alias + schema), so a
    /// console can populate a type picker without a separate metadata round-trip.
    /// </summary>
    Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token = default);

    /// <summary>
    /// A page of documents of the named type, each as raw JSON plus its metadata, and the total matching
    /// count.
    /// </summary>
    /// <remarks>
    /// An implementation that does not understand a criterion on <paramref name="options"/> — most likely
    /// <see cref="DocumentQueryOptions.Where"/> or <see cref="DocumentQueryOptions.OrderBy"/> — must throw
    /// <see cref="DocumentCriteriaNotSupportedException"/>. Silently returning the unfiltered page is the
    /// one wrong answer: a console cannot tell it apart from a filter that matched everything.
    /// </remarks>
    Task<DocumentQueryResult> QueryDocumentsAsync(
        string documentTypeName, DocumentQueryOptions options, CancellationToken token = default);

    /// <summary>
    /// One document of the named type by its string id, as raw JSON, or <see langword="null"/> when
    /// not found. Reads the default tenant.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="LoadDocumentAsync"/>, which takes a tenant and returns metadata. The
    /// default implementation forwards there, so a store implements the load once.
    /// </remarks>
    async Task<string?> LoadDocumentJsonAsync(
        string documentTypeName, string id, CancellationToken token = default)
        => (await LoadDocumentAsync(documentTypeName, id, null, token).ConfigureAwait(false))?.Json;

    /// <summary>
    /// One document of the named type by its string id, with its metadata, or <see langword="null"/>
    /// when not found (jasperfx#870 §3).
    /// </summary>
    /// <param name="documentTypeName">The document type — see the type-name rule on the interface.</param>
    /// <param name="id">The id as text; converted to the stored identity type before comparison.</param>
    /// <param name="tenantId">
    /// The tenant to read. Null, empty or whitespace means the default tenant, exactly as it does for
    /// <see cref="DocumentQueryOptions.TenantId"/>.
    /// </param>
    /// <param name="token">Cancellation.</param>
    Task<StoredDocument?> LoadDocumentAsync(
        string documentTypeName, string id, string? tenantId, CancellationToken token = default);
}

/// <summary>A queryable document type on a store: CLR type name, table alias, and schema.</summary>
public record DocumentTypeRef(string TypeName, string Alias, string SchemaName);

/// <summary>
/// Options for <see cref="IDocumentStoreDiagnostics.QueryDocumentsAsync"/>. Paging is required; the
/// optional <see cref="IdEquals"/> narrows to a single id. Everything else is an init-only member so the
/// record stays binary-compatible as it grows.
/// </summary>
public record DocumentQueryOptions(int PageNumber, int PageSize, string? IdEquals = null)
{
    /// <summary>
    /// Optional tenant id to scope the query to. For conjoined tenancy the implementation filters by the
    /// store's tenant column; for database-per-tenant it targets that tenant's physical database. Null,
    /// empty or whitespace queries the default tenant — see <see cref="NormalizeTenantId"/>. Added as an
    /// init-only member (not a positional parameter) so the record stays binary-compatible. See
    /// JasperFx/CritterWatch EVENT_STORE_EXPLORER_PLAN §3.1.
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

    /// <summary>
    /// Optional predicate over the document's own members, as Dynamic LINQ text — e.g.
    /// <c>Status = "Open" and Total &gt; @0</c> (jasperfx#870 §1, translated per jasperfx#869).
    /// </summary>
    /// <remarks>
    /// The store applies it to its own <c>IQueryable&lt;T&gt;</c> for the named type, so member casing,
    /// enum storage, tenancy, soft deletes and hierarchy filtering stay the store's job rather than a
    /// consumer's hand-built SQL. Prefer typed <see cref="Arguments"/> (<c>@0</c>, <c>@1</c>, …) over
    /// literals: date literals parse in the server's local time zone. A store that cannot apply a
    /// predicate — no predicate support at all, or this one fails to parse or translate — throws
    /// <see cref="DocumentCriteriaNotSupportedException"/>.
    /// </remarks>
    public string? Where { get; init; }

    /// <summary>
    /// Optional ordering over the document's own members, as Dynamic LINQ text — e.g.
    /// <c>PlacedAt desc, Id</c>. Null leaves the store's own stable order (by id). A store that cannot
    /// apply an ordering throws <see cref="DocumentCriteriaNotSupportedException"/>.
    /// </summary>
    public string? OrderBy { get; init; }

    /// <summary>
    /// Typed values for the <c>@0</c>, <c>@1</c>, … placeholders in <see cref="Where"/>. Ignored when
    /// <see cref="Where"/> is null.
    /// </summary>
    public object?[]? Arguments { get; init; }

    /// <summary>
    /// Include soft-deleted rows. Default <see langword="false"/>: a soft-deleted document is not a live
    /// one, and a console showing it as live is the bug jasperfx#870 §3 recorded on two of three stores.
    /// Irrelevant for a type that hard-deletes.
    /// </summary>
    public bool IncludeSoftDeleted { get; init; }

    /// <summary>
    /// The tenant a store should actually read: <see langword="null"/> — the default tenant — for a null,
    /// empty or whitespace id, the id itself otherwise. CritterWatch#1304 measured an empty string read
    /// as a tenant <em>named</em> <c>""</c> (zero rows on conjoined Marten); this is the one definition
    /// every store applies.
    /// </summary>
    public static string? NormalizeTenantId(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
}

/// <summary>
/// A page of stored documents with the total matching count for pager UIs.
/// </summary>
/// <remarks>
/// <see cref="DocumentsJson"/> is the original, JSON-only shape and is kept for existing consumers.
/// <see cref="Documents"/> carries the same rows, in the same order, with their metadata (jasperfx#870 §4);
/// build the result through the <see cref="DocumentQueryResult(IReadOnlyList{StoredDocument},long,int,int)"/>
/// constructor and both are populated. An implementation predating #870 leaves <see cref="Documents"/>
/// empty, so a consumer that finds it empty while <see cref="DocumentsJson"/> is not should fall back.
/// </remarks>
public record DocumentQueryResult(IReadOnlyList<string> DocumentsJson, long TotalCount, int PageNumber, int PageSize)
{
    /// <summary>
    /// Build a page from metadata-bearing rows, deriving <see cref="DocumentsJson"/> from them.
    /// </summary>
    public DocumentQueryResult(IReadOnlyList<StoredDocument> documents, long totalCount, int pageNumber, int pageSize)
        : this(documents.Select(x => x.Json).ToList(), totalCount, pageNumber, pageSize)
    {
        Documents = documents;
    }

    /// <summary>
    /// The page's rows with their metadata, parallel to <see cref="DocumentsJson"/>.
    /// </summary>
    public IReadOnlyList<StoredDocument> Documents { get; init; } = [];
}

/// <summary>
/// One stored document as a console sees it: its id and raw JSON, plus the metadata columns every
/// document table on every Critter Stack store carries (jasperfx#870 §4).
/// </summary>
/// <param name="Id">The document's identity as text.</param>
/// <param name="Json">The stored JSON, exactly as the store persisted it.</param>
public sealed record StoredDocument(string Id, string Json)
{
    /// <summary>
    /// The concurrency token, as opaque text: the Guid version's string form, or the numeric revision's.
    /// A console echoes it back as <see cref="DocumentWriteRequest.ExpectedVersion"/> unchanged.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>When the document was last written.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>When the document was first written, where the store tracks it; otherwise null.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>
    /// The tenant the row belongs to. The store's default tenant id for a single-tenanted type.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>Whether the row is soft-deleted. Always false for a type that hard-deletes.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>When the row was soft-deleted, or null.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>
    /// The .NET type of this row as its <c>FullNameInCode()</c> — the sub-class for a row in a hierarchy,
    /// the mapped type otherwise.
    /// </summary>
    public string? DocumentType { get; init; }
}

/// <summary>
/// Thrown by an <see cref="IDocumentStoreDiagnostics"/> implementation asked to apply a query criterion it
/// cannot honor (jasperfx#870 §1). The refusal is the contract: returning the unfiltered page instead is
/// indistinguishable, to a console, from a filter that matched every row.
/// </summary>
public class DocumentCriteriaNotSupportedException : NotSupportedException
{
    /// <param name="criterion">The <see cref="DocumentQueryOptions"/> member that could not be applied.</param>
    /// <param name="reason">Why — shown to the operator, so say what to change.</param>
    /// <param name="inner">The underlying parse or translation failure, if any.</param>
    public DocumentCriteriaNotSupportedException(string criterion, string reason, Exception? inner = null)
        : base($"The document query criterion '{criterion}' is not supported: {reason}", inner)
    {
        Criterion = criterion;
    }

    /// <summary>The <see cref="DocumentQueryOptions"/> member that could not be applied.</summary>
    public string Criterion { get; }
}
