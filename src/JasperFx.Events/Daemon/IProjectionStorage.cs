using JasperFx.Events.Aggregation;

namespace JasperFx.Events.Daemon;

public interface IProjectionStorage<TDoc, TId> : IIdentitySetter<TDoc, TId>
{
    // This will wrap ProjectionUpdateBatch & the right DocumentSession

    string TenantId { get; }

    /// <summary>
    /// Can the daemon apply several of a range's slices concurrently against this storage instance?
    /// Return <see langword="false" /> when it cannot, and the runner applies them one at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// jasperfx#683. <c>AggregationRunner</c> applies every slice in a range through a fixed 10-wide
    /// block, and every one of them gets the <em>same</em> storage instance. That is what the
    /// products' own document storage is built for. It is not what an EF Core storage can take: it
    /// wraps a single <c>DbContext</c> per tenant/batch, and a <c>DbContext</c> is not thread-safe.
    /// A multi-stream projection with custom grouping fans one event out into many slices, so up to
    /// ten of them concurrently call <c>Entry()</c> / <c>FindAsync</c> and mutate the same change
    /// tracker — reported as an <c>InvalidOperationException</c> out of <c>Dictionary.TryInsert</c>
    /// and a <c>NullReferenceException</c> out of <c>ChangeDetector.DetectChanges</c>
    /// (<see href="https://github.com/JasperFx/marten/issues/5266" />).
    /// </para>
    /// <para>
    /// The declaration belongs here rather than on <c>AsyncOptions</c> because the storage is the
    /// thing that is or is not safe, and it already knows. A parallelism knob would work, but it
    /// would make correctness a configuration problem that a user has to know they have.
    /// </para>
    /// <para>
    /// ⚠️ Serializing calls <em>inside</em> a storage implementation does not substitute for this.
    /// A lock around each storage member still leaves the aggregation on one thread mutating
    /// entities while another thread's <c>Entry()</c> runs change detection over them, which is
    /// exactly the reported <c>ChangeDetector</c> failure. The fan-out itself has to stop, and
    /// nothing reachable from inside the storage can stop it.
    /// </para>
    /// <para>
    /// Defaults to <see langword="true" />, so this is additive: no existing storage has to change
    /// to stay correct.
    /// </para>
    /// </remarks>
    bool IsThreadSafe => true;
    
    void HardDelete(TDoc snapshot);
    void UnDelete(TDoc snapshot);
    void Store(TDoc snapshot);
    void Delete(TId identity);
    
    void HardDelete(TDoc snapshot, string tenantId);
    void UnDelete(TDoc snapshot, string tenantId);
    void Store(TDoc snapshot, TId id, string tenantId);
    void Delete(TId identity, string tenantId);

    Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities, CancellationToken cancellationToken);

    void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope);
    void ArchiveStream(TId sliceId, string tenantId);
    Task<TDoc> LoadAsync(TId id, CancellationToken cancellation);

    /// <summary>
    /// jasperfx#525: store a projected document as part of a deferred rebuild flush. When
    /// <paramref name="previouslyFlushed"/> is false the aggregate is appearing for the first time this
    /// rebuild (post-TRUNCATE), so a store may route it through an INSERT-only fast path (e.g. binary COPY);
    /// when true the aggregate was already written in an earlier flush window (an overflow reflush) and must
    /// be routed as an UPSERT. The default implementation ignores the hint and behaves exactly like
    /// <see cref="StoreProjection"/>, so a store that has not opted into the optimization stays correct.
    /// </summary>
    void StoreProjectionForRebuildFlush(TDoc aggregate, IEvent? lastEvent, AggregationScope scope,
        bool previouslyFlushed)
        => StoreProjection(aggregate, lastEvent, scope);
}

public static class ProjectionStorageExtensions
{
    public static void ApplyInline<TDoc, TId>(this IProjectionStorage<TDoc, TId> storage, TDoc? snapshot,
        ActionType action, TId id, string tenantId)
    {
        switch (action)
        {
            case ActionType.Delete:
                storage.Delete(id, tenantId);
                break;
            case ActionType.Store:
                storage.Store(snapshot!, id, tenantId);
                break;
            case ActionType.HardDelete:
                storage.HardDelete(snapshot!, tenantId);
                break;
            case ActionType.UnDeleteAndStore:
                storage.UnDelete(snapshot!, tenantId);
                storage.Store(snapshot!, id, tenantId);
                break;
            case ActionType.StoreThenSoftDelete:
                storage.Store(snapshot!, id, tenantId);
                storage.Delete(id, tenantId);
                break;
        }
    }
}