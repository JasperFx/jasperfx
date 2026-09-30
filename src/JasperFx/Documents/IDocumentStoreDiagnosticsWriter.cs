namespace JasperFx.Documents;

/// <summary>
/// The write-side sibling of <see cref="IDocumentStoreDiagnostics"/> (jasperfx#870 §6): save a document
/// from JSON, or delete one, by type name and id, from a console that cannot reference the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate interface on purpose.</b> <see cref="IDocumentStoreDiagnostics"/> stays read-only, so a
/// host can register one without the other and a console can offer browsing without editing.
/// </para>
/// <para>
/// <b>Store-owned, and through the store's normal pipeline.</b> There is no serializer abstraction to
/// turn the JSON into a document outside the store, and a raw <c>UPDATE</c> of the JSON column would
/// leave duplicated-field columns stale and skip versioning. An implementation deserializes with the
/// store's own serializer and writes through a session, so a save here is indistinguishable from one an
/// application made — including soft deletes, which a <see cref="DeleteDocumentAsync"/> honors.
/// </para>
/// <para>
/// <b>Concurrency.</b> With <see cref="DocumentWriteRequest.ExpectedVersion"/> set, a write whose version
/// is stale is refused with <see cref="DocumentWriteStatus.ConcurrencyConflict"/> and the current
/// document, so a console can show what changed rather than overwrite it. The version is the opaque
/// <see cref="StoredDocument.Version"/> a read returned, and it is checked whether or not the type opted
/// into optimistic concurrency — a console asking for a guarded write gets one.
/// </para>
/// <para>
/// Tenant ids follow <see cref="DocumentQueryOptions.NormalizeTenantId"/>, and type names follow the rule
/// on <see cref="IDocumentStoreDiagnostics"/>. An unknown type name is an
/// <see cref="ArgumentException"/> here rather than an empty answer: a write that did nothing must not
/// look like one that succeeded.
/// </para>
/// </remarks>
public interface IDocumentStoreDiagnosticsWriter
{
    /// <summary>
    /// Stable URI identifying the store this surface writes — the same value as the paired
    /// <see cref="IDocumentStoreDiagnostics.Subject"/>.
    /// </summary>
    Uri Subject { get; }

    /// <summary>
    /// Insert or replace one document from its JSON.
    /// </summary>
    /// <remarks>
    /// The id in the JSON must agree with <see cref="DocumentWriteRequest.Id"/>; a mismatch is an
    /// <see cref="ArgumentException"/> rather than a guess at which one the operator meant. Returns
    /// <see cref="DocumentWriteStatus.Saved"/> with the document as stored — new version included.
    /// </remarks>
    Task<DocumentWriteResult> SaveDocumentJsonAsync(DocumentWriteRequest request, CancellationToken token = default);

    /// <summary>
    /// Delete one document — a soft delete for a type configured that way.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="DocumentWriteStatus.Deleted"/>, <see cref="DocumentWriteStatus.NotFound"/> when
    /// there is no live row with that id, or <see cref="DocumentWriteStatus.ConcurrencyConflict"/> with the
    /// current document when the expected version is stale.
    /// </remarks>
    Task<DocumentWriteResult> DeleteDocumentAsync(DocumentDeleteRequest request, CancellationToken token = default);
}

/// <summary>A request to save one document from JSON.</summary>
/// <param name="DocumentTypeName">The document type — see the type-name rule on <see cref="IDocumentStoreDiagnostics"/>.</param>
/// <param name="Id">The document's identity as text.</param>
/// <param name="Json">The document, serialized as the store itself would serialize it.</param>
public sealed record DocumentWriteRequest(string DocumentTypeName, string Id, string Json)
{
    /// <summary>
    /// The <see cref="StoredDocument.Version"/> the caller last read. Null writes unconditionally;
    /// set, a stale version is refused.
    /// </summary>
    public string? ExpectedVersion { get; init; }

    /// <summary>The tenant to write. Null, empty or whitespace means the default tenant.</summary>
    public string? TenantId { get; init; }
}

/// <summary>A request to delete one document.</summary>
/// <param name="DocumentTypeName">The document type — see the type-name rule on <see cref="IDocumentStoreDiagnostics"/>.</param>
/// <param name="Id">The document's identity as text.</param>
public sealed record DocumentDeleteRequest(string DocumentTypeName, string Id)
{
    /// <inheritdoc cref="DocumentWriteRequest.ExpectedVersion"/>
    public string? ExpectedVersion { get; init; }

    /// <inheritdoc cref="DocumentWriteRequest.TenantId"/>
    public string? TenantId { get; init; }
}

/// <summary>What a diagnostic write did.</summary>
public enum DocumentWriteStatus
{
    /// <summary>The document was inserted or replaced.</summary>
    Saved,

    /// <summary>The document was deleted (or soft-deleted).</summary>
    Deleted,

    /// <summary>There was no live document with that id to delete.</summary>
    NotFound,

    /// <summary>
    /// The expected version was stale — or the document the caller expected no longer exists. Nothing was
    /// written.
    /// </summary>
    ConcurrencyConflict
}

/// <summary>The outcome of a diagnostic write.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Document">
/// After <see cref="DocumentWriteStatus.Saved"/>, the document as stored. After
/// <see cref="DocumentWriteStatus.ConcurrencyConflict"/>, the <em>current</em> document, or null if it is
/// gone. Otherwise null.
/// </param>
public sealed record DocumentWriteResult(DocumentWriteStatus Status, StoredDocument? Document = null);
