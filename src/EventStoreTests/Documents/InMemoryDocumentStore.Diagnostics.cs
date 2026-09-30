using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using JasperFx.Events;
using JasperFx.Events.ComplianceTests;

namespace EventStoreTests.Documents;

/// <summary>
/// The reference <see cref="IDocumentStoreDiagnostics" /> and <see cref="IDocumentStoreDiagnosticsWriter" />
/// (jasperfx#870), plus the row metadata, soft deletes and hierarchies they are defined over.
/// </summary>
/// <remarks>
/// <para>
/// Here for the reason every other capability in this double is: <c>DocumentStoreDiagnosticsCompliance</c>
/// should be run by something before Marten, Polecat and Fisher are held to it. The metadata lives beside
/// the documents rather than on them, which is the shape every real store has — columns next to the JSON.
/// </para>
/// <para>
/// <b>Criteria are refused.</b> There is no Dynamic LINQ here (that is jasperfx#869), so
/// <see cref="DocumentQueryOptions.Where" /> and <see cref="DocumentQueryOptions.OrderBy" /> throw
/// <see cref="DocumentCriteriaNotSupportedException" /> — which is itself the contract for a store without
/// predicate support, and what the suite's refusal fact runs against.
/// </para>
/// </remarks>
public partial class InMemoryDocumentStore : IDocumentStoreDiagnostics, IDocumentStoreDiagnosticsWriter,
    IDocumentStoreUsageSource
{
    private readonly ConcurrentDictionary<(Type Root, string Tenant, object Id), RowMetadata> _metadata = new();

    /// <summary>Every declared document type — the replay of <see cref="DocumentComplianceConfig.DocumentTypes" />.</summary>
    public HashSet<Type> DocumentTypes { get; } = new();

    /// <summary>Soft-deleted roots — the replay of <see cref="DocumentComplianceConfig.SoftDeletedDocuments" />.</summary>
    public HashSet<Type> SoftDeletedTypes { get; } = new();

    /// <summary>Sub-class → root — the replay of <see cref="DocumentComplianceConfig.SubClasses" />.</summary>
    public Dictionary<Type, Type> SubClassRoots { get; } = new();

    public Uri Subject { get; } = new("inmemory://main");

    internal Type RootOf(Type documentType) => SubClassRoots.GetValueOrDefault(documentType, documentType);

    internal bool IsDeleted(Type documentType, string scope, object id)
        => _metadata.TryGetValue((RootOf(documentType), scope, id), out var row) && row.IsDeleted;

    internal void RecordWrite(Type documentType, string scope, object id, Type runtimeType)
    {
        var now = DateTimeOffset.UtcNow;
        _metadata.AddOrUpdate((RootOf(documentType), scope, id),
            _ => new RowMetadata(Guid.NewGuid(), now, now, runtimeType, false, null),
            (_, existing) => existing with
            {
                Version = Guid.NewGuid(), LastModified = now, DocumentType = runtimeType, IsDeleted = false,
                DeletedAt = null
            });
    }

    /// <summary>
    /// A session delete: a soft delete for a type declared that way, a real removal otherwise.
    /// </summary>
    internal void Remove(Type documentType, string tenantId, object id)
    {
        var root = RootOf(documentType);
        var scope = ScopeFor(root, tenantId);

        if (SoftDeletedTypes.Contains(root))
        {
            var now = DateTimeOffset.UtcNow;
            _metadata.AddOrUpdate((root, scope, id),
                _ => new RowMetadata(Guid.NewGuid(), now, now, documentType, true, now),
                (_, existing) => existing with { Version = Guid.NewGuid(), LastModified = now, IsDeleted = true, DeletedAt = now });
            return;
        }

        StorageFor(root, tenantId).TryRemove(id, out _);
        _metadata.TryRemove((root, scope, id), out _);
    }

    Task<DocumentStoreUsage?> IDocumentStoreUsageSource.TryCreateUsage(CancellationToken token)
        => Task.FromResult<DocumentStoreUsage?>(null);

    // ------------------------------------------------------------------ reads

    Task<IReadOnlyList<DocumentTypeRef>> IDocumentStoreDiagnostics.DocumentTypesAsync(CancellationToken token)
        => Task.FromResult<IReadOnlyList<DocumentTypeRef>>(DocumentTypes
            .Where(x => !SubClassRoots.ContainsKey(x))
            .Select(x => new DocumentTypeRef(x.FullNameInCode(), x.Name.ToLowerInvariant(), "inmemory"))
            .OrderBy(x => x.TypeName, StringComparer.Ordinal)
            .ToList());

    Task<DocumentQueryResult> IDocumentStoreDiagnostics.QueryDocumentsAsync(string documentTypeName,
        DocumentQueryOptions options, CancellationToken token)
    {
        if (options.Where is not null)
        {
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.Where),
                "the in-memory reference store has no predicate translation (jasperfx#869).");
        }

        if (options.OrderBy is not null)
        {
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.OrderBy),
                "the in-memory reference store has no ordering translation (jasperfx#869).");
        }

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Max(1, options.PageSize);

        if (Resolve(documentTypeName) is not { } requested)
        {
            return Task.FromResult(new DocumentQueryResult((IReadOnlyList<StoredDocument>)[], 0, pageNumber, pageSize));
        }

        var rows = RowsOf(requested, options.TenantId)
            .Where(x => options.IncludeSoftDeleted || !x.IsDeleted);

        if (options.IdEquals is { } idText)
        {
            var id = ConvertIdentity(requested, idText);
            rows = rows.Where(x => Equals(x.RawId, id));
        }

        var all = rows.OrderBy(x => x.Document.Id, StringComparer.Ordinal).ToList();
        var page = all.Skip((pageNumber - 1) * pageSize).Take(pageSize).Select(x => x.Document).ToList();

        return Task.FromResult(new DocumentQueryResult(page, all.Count, pageNumber, pageSize));
    }

    Task<StoredDocument?> IDocumentStoreDiagnostics.LoadDocumentAsync(string documentTypeName, string id,
        string? tenantId, CancellationToken token)
        => Task.FromResult(Find(documentTypeName, id, tenantId)?.Document);

    private Type? Resolve(string documentTypeName)
        => DocumentTypes.Concat(SubClassRoots.Keys)
            .FirstOrDefault(x => string.Equals(x.FullNameInCode(), documentTypeName, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(x.Name, documentTypeName, StringComparison.OrdinalIgnoreCase));

    private sealed record Row(object RawId, bool IsDeleted, StoredDocument Document);

    /// <summary>
    /// Every row, deleted or not, that is a <paramref name="requested" /> — soft deletes are the caller's
    /// decision, hierarchy filtering is not.
    /// </summary>
    private IEnumerable<Row> RowsOf(Type requested, string? tenantId)
    {
        var root = RootOf(requested);
        var tenant = DocumentQueryOptions.NormalizeTenantId(tenantId) ?? StorageConstants.DefaultTenantId;
        var scope = ScopeFor(root, tenant);

        foreach (var (id, document) in StorageFor(root, tenant))
        {
            if (!requested.IsInstanceOfType(document)) continue;
            if (!_metadata.TryGetValue((root, scope, id), out var meta)) continue;

            yield return new Row(id, meta.IsDeleted, new StoredDocument(id.ToString()!,
                JsonSerializer.Serialize(document, document.GetType()))
            {
                Version = meta.Version.ToString(),
                LastModified = meta.LastModified,
                Created = meta.Created,
                TenantId = scope,
                IsDeleted = meta.IsDeleted,
                DeletedAt = meta.DeletedAt,
                DocumentType = meta.DocumentType.FullNameInCode()
            });
        }
    }

    private Row? Find(string documentTypeName, string id, string? tenantId)
    {
        if (Resolve(documentTypeName) is not { } requested) return null;

        var raw = ConvertIdentity(requested, id);
        return RowsOf(requested, tenantId).FirstOrDefault(x => Equals(x.RawId, raw));
    }

    /// <summary>
    /// The text id converted to the stored identity type — the rule jasperfx#870 §3 set, and the reason an
    /// upper-case Guid still matches.
    /// </summary>
    private static object ConvertIdentity(Type documentType, string id)
    {
        var idType = documentType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;

        if (idType == typeof(Guid) && Guid.TryParse(id, out var guid)) return guid;
        if (idType == typeof(int) && int.TryParse(id, out var i)) return i;
        if (idType == typeof(long) && long.TryParse(id, out var l)) return l;

        return id;
    }

    // ------------------------------------------------------------------ writes

    async Task<DocumentWriteResult> IDocumentStoreDiagnosticsWriter.SaveDocumentJsonAsync(DocumentWriteRequest request,
        CancellationToken token)
    {
        var requested = ResolveForWrite(request.DocumentTypeName);
        var tenant = DocumentQueryOptions.NormalizeTenantId(request.TenantId) ?? StorageConstants.DefaultTenantId;

        var document = JsonSerializer.Deserialize(request.Json, requested)
                       ?? throw new ArgumentException("The JSON deserialized to null.", nameof(request));

        var expectedId = ConvertIdentity(requested, request.Id);
        var actualId = document.GetType().GetProperty("Id")?.GetValue(document);
        if (!Equals(actualId, expectedId))
        {
            throw new ArgumentException(
                $"The id in the JSON ({actualId}) does not match the requested id ({request.Id}).",
                nameof(request));
        }

        if (request.ExpectedVersion is not null && Conflict(request.DocumentTypeName, request.Id, tenant,
                request.ExpectedVersion) is { } conflict)
        {
            return conflict;
        }

        // Through a session — the store's normal pipeline — never by writing the bucket directly.
        await using (var session = LightweightSession(tenant))
        {
            _store.MakeGenericMethod(requested).Invoke(session, [ToArray(requested, document)]);
            await session.SaveChangesAsync(token).ConfigureAwait(false);
        }

        return new DocumentWriteResult(DocumentWriteStatus.Saved,
            Find(request.DocumentTypeName, request.Id, tenant)?.Document);
    }

    async Task<DocumentWriteResult> IDocumentStoreDiagnosticsWriter.DeleteDocumentAsync(DocumentDeleteRequest request,
        CancellationToken token)
    {
        var requested = ResolveForWrite(request.DocumentTypeName);
        var tenant = DocumentQueryOptions.NormalizeTenantId(request.TenantId) ?? StorageConstants.DefaultTenantId;

        var current = Find(request.DocumentTypeName, request.Id, tenant);
        if (current is null || current.IsDeleted)
        {
            return new DocumentWriteResult(DocumentWriteStatus.NotFound);
        }

        if (request.ExpectedVersion is not null && Conflict(request.DocumentTypeName, request.Id, tenant,
                request.ExpectedVersion) is { } conflict)
        {
            return conflict;
        }

        await using (var session = LightweightSession(tenant))
        {
            var id = current.RawId;
            var delete = id is Guid ? _deleteByGuid : _deleteByString;
            delete.MakeGenericMethod(requested).Invoke(session, [id is Guid ? id : id.ToString()]);
            await session.SaveChangesAsync(token).ConfigureAwait(false);
        }

        return new DocumentWriteResult(DocumentWriteStatus.Deleted);
    }

    private Type ResolveForWrite(string documentTypeName)
        => Resolve(documentTypeName)
           ?? throw new ArgumentException($"Unknown document type '{documentTypeName}'.", nameof(documentTypeName));

    /// <summary>
    /// Not atomic with the write that follows — acceptable in a single-process test double, and exactly the
    /// part a real store does inside its transaction.
    /// </summary>
    private DocumentWriteResult? Conflict(string documentTypeName, string id, string tenant, string expectedVersion)
    {
        var current = Find(documentTypeName, id, tenant);
        return current is not null && !current.IsDeleted && current.Document.Version == expectedVersion
            ? null
            : new DocumentWriteResult(DocumentWriteStatus.ConcurrencyConflict,
                current is { IsDeleted: false } ? current.Document : null);
    }

    private static Array ToArray(Type type, object document)
    {
        var array = Array.CreateInstance(type, 1);
        array.SetValue(document, 0);
        return array;
    }

    private static readonly MethodInfo _store = typeof(InMemoryDocumentSession).GetMethod(nameof(InMemoryDocumentSession.Store))!;

    private static readonly MethodInfo _deleteByGuid = typeof(InMemoryDocumentSession).GetMethods()
        .Single(x => x.Name == nameof(InMemoryDocumentSession.Delete) && x.GetParameters()[0].ParameterType == typeof(Guid));

    private static readonly MethodInfo _deleteByString = typeof(InMemoryDocumentSession).GetMethods()
        .Single(x => x.Name == nameof(InMemoryDocumentSession.Delete) && x.GetParameters()[0].ParameterType == typeof(string));

    private sealed record RowMetadata(
        Guid Version,
        DateTimeOffset LastModified,
        DateTimeOffset Created,
        Type DocumentType,
        bool IsDeleted,
        DateTimeOffset? DeletedAt);
}
