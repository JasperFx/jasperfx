using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory.Projections;

namespace JasperFx.Events.InMemory.Daemon;

/// <summary>
/// Projection storage for an async projection batch on the in-memory prototyping store (jasperfx#1201). Reads go
/// straight to the store; writes are queued on the batch's session and applied when the batch commits, with the
/// shard's progression, in the order they were made.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Prototyping store; not AOT-compatible.")]
internal sealed class InMemoryQueuedProjectionStorage<TDoc, TId> : IProjectionStorage<TDoc, TId>
{
    private readonly InMemoryDocumentStore _store;
    private readonly InMemoryDocumentSession _session;

    // Reads and the identity members run against the store directly; writes go through a copy of this per commit
    private readonly InMemoryProjectionStorage<TDoc, TId> _reader;

    internal InMemoryQueuedProjectionStorage(InMemoryDocumentStore store, InMemoryDocumentSession session,
        string tenantId)
    {
        _store = store;
        _session = session;
        TenantId = tenantId;
        _reader = new InMemoryProjectionStorage<TDoc, TId>(store, new InMemoryChangeSet(), tenantId);
    }

    public string TenantId { get; }

    public void SetIdentity(TDoc document, TId identity) => _reader.SetIdentity(document, identity);

    public TId Identity(TDoc document) => _reader.Identity(document);

    private void enqueue(Action<InMemoryProjectionStorage<TDoc, TId>> write)
        => _session.EnqueueProjectionWrite(changes =>
            write(new InMemoryProjectionStorage<TDoc, TId>(_store, changes, TenantId)));

    // A copy now, so a cached aggregate the daemon keeps folding cannot change what this batch writes
    private static TDoc copy(TDoc document) => InMemoryDocumentStore.Copy<TDoc>(document!)!;

    public void HardDelete(TDoc snapshot)
    {
        var copied = copy(snapshot);
        enqueue(x => x.HardDelete(copied));
    }

    public void UnDelete(TDoc snapshot)
    {
    }

    public void Store(TDoc snapshot)
    {
        var copied = copy(snapshot);
        enqueue(x => x.Store(copied));
    }

    public void Delete(TId identity) => enqueue(x => x.Delete(identity));

    public void HardDelete(TDoc snapshot, string tenantId)
    {
        var copied = copy(snapshot);
        enqueue(x => x.HardDelete(copied, tenantId));
    }

    public void UnDelete(TDoc snapshot, string tenantId)
    {
    }

    public void Store(TDoc snapshot, TId id, string tenantId)
    {
        var copied = copy(snapshot);
        enqueue(x => x.Store(copied, id, tenantId));
    }

    public void Delete(TId identity, string tenantId) => enqueue(x => x.Delete(identity, tenantId));

    public void HardDeleteById(TId identity) => enqueue(x => x.HardDeleteById(identity));

    public void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope)
    {
        var copied = copy(aggregate);
        enqueue(x => x.StoreProjection(copied, lastEvent, scope));
    }

    public void ArchiveStream(TId sliceId, string tenantId) => _reader.ArchiveStream(sliceId, tenantId);

    public Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities, CancellationToken cancellationToken)
    {
        using (_store.EnterReadGate())
        {
            return _reader.LoadManyAsync(identities, cancellationToken);
        }
    }

    public Task<TDoc> LoadAsync(TId id, CancellationToken cancellation)
    {
        using (_store.EnterReadGate())
        {
            return _reader.LoadAsync(id, cancellation);
        }
    }
}
