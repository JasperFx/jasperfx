using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.InMemory.Projections;

/// <summary>
/// Where an inline projection's documents go on the in-memory prototyping store (jasperfx#964).
/// </summary>
/// <remarks>
/// Only ever handed out inside a session's commit, so it writes straight into the store: the commit's
/// snapshot already covers a rollback, and every write is recorded on the commit's change set so the
/// commit listeners see projected documents alongside the caller's own.
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Prototyping store; not AOT-compatible.")]
internal sealed class InMemoryProjectionStorage<TDoc, TId> : IProjectionStorage<TDoc, TId>
{
    private readonly InMemoryDocumentStore _store;
    private readonly InMemoryChangeSet _changes;

    internal InMemoryProjectionStorage(InMemoryDocumentStore store, InMemoryChangeSet changes, string tenantId)
    {
        _store = store;
        _changes = changes;
        TenantId = tenantId;
    }

    public string TenantId { get; }

    // ---- identity ----

    public void SetIdentity(TDoc document, TId identity)
    {
        switch (idMember())
        {
            case PropertyInfo { CanWrite: true } property:
                property.SetValue(document, identity);
                break;
            case FieldInfo { IsInitOnly: false } field:
                field.SetValue(document, identity);
                break;
        }
    }

    public TId Identity(TDoc document)
        => idMember() switch
        {
            PropertyInfo property => (TId)property.GetValue(document)!,
            FieldInfo field => (TId)field.GetValue(document)!,
            _ => throw new InvalidOperationException(
                $"{typeof(TDoc).FullName} has no identity member for the in-memory prototyping store to read.")
        };

    private static MemberInfo? idMember() => InMemoryAggregateIdentity.FindIdMember(typeof(TDoc));

    // ---- writes ----

    public void Store(TDoc snapshot) => Store(snapshot, Identity(snapshot), TenantId);

    public void Store(TDoc snapshot, TId id, string tenantId)
    {
        var key = (object)id!;
        var document = (object)snapshot!;
        var storage = _store.StorageFor(typeof(TDoc), tenantId);
        var existed = storage.ContainsKey(key) && !_store.IsDeleted(typeof(TDoc), scope(tenantId), key);

        // A copy, as the session stores one: what is stored must not change if the projection's instance does
        storage[key] = InMemoryDocumentStore.Copy<TDoc>(document)!;
        _store.RecordWrite(typeof(TDoc), scope(tenantId), key, document.GetType());

        if (existed)
        {
            _changes.RecordUpdated(document);
        }
        else
        {
            _changes.RecordInserted(document);
        }
    }

    public void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope)
        => Store(aggregate, Identity(aggregate), TenantId);

    public void Delete(TId identity) => Delete(identity, TenantId);

    public void Delete(TId identity, string tenantId)
    {
        _store.Remove(typeof(TDoc), tenantId, identity!);
        _changes.RecordDeleted(typeof(TDoc), identity);
    }

    public void HardDelete(TDoc snapshot) => HardDelete(snapshot, TenantId);

    public void HardDelete(TDoc snapshot, string tenantId)
    {
        var id = Identity(snapshot);
        _store.HardRemove(typeof(TDoc), tenantId, id!);
        _changes.RecordDeleted(typeof(TDoc), id);
    }

    // Storing a row clears its soft-deleted mark, and UnDelete is always followed by a Store
    public void UnDelete(TDoc snapshot)
    {
    }

    public void UnDelete(TDoc snapshot, string tenantId)
    {
    }

    public void ArchiveStream(TId sliceId, string tenantId)
        => throw InMemoryEventRegistry.NotSupported("Archiving a stream from a projection");

    // ---- reads: what the commit in flight has written so far ----

    public Task<TDoc> LoadAsync(TId id, CancellationToken cancellation)
        => Task.FromResult(load(id, TenantId)!);

    public Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities, CancellationToken cancellationToken)
    {
        // TId is unconstrained by the storage contract; identities handed in are never null
#pragma warning disable CS8714
        var found = new Dictionary<TId, TDoc>();
#pragma warning restore CS8714
        foreach (var id in identities.Distinct())
        {
            if (load(id, TenantId) is { } document) found[id] = document;
        }

        return Task.FromResult<IReadOnlyDictionary<TId, TDoc>>(found);
    }

    private TDoc? load(TId id, string tenantId)
        => _store.StorageFor(typeof(TDoc), tenantId).TryGetValue(id!, out var stored)
           && stored is TDoc
           && !_store.IsDeleted(typeof(TDoc), scope(tenantId), id!)
            ? InMemoryDocumentStore.Copy<TDoc>(stored)
            : default;

    private string scope(string tenantId) => _store.ScopeFor(typeof(TDoc), tenantId);
}
