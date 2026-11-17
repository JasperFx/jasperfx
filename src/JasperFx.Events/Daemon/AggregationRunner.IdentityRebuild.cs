using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

// jasperfx#1201: per-identity rebuild of a single-stream projection, bound to the next batch the shard builds.
//
// There is no hold window. A request waits for the next batch boundary, and that batch re-folds the identity
// from nothing over every event of its stream up to the batch's ceiling C, takes the identity out of its own
// page, and commits the result with progression C. "Nothing lost" and "nothing applied twice" both follow from
// what the daemon already guarantees: a batch writes its documents and moves progression together, and the
// batches of one shard are built and committed one after another, in order. Every later page applies only events
// above C, on top of the re-fold, from the cache or from storage.
public partial class AggregationRunner<TDoc, TId, TOperations, TQuerySession>
{
    // Null until the first request this runner ever takes, and the only thing BuildBatchAsync reads when nothing is
    // pending. After that, one volatile int read per batch.
    private IdentityRebuildState? _identityRebuilds;
    private readonly object _identityRebuildLock = new();
    private int _identityRebuildPrefolds;

    // How many long-stream pre-folds this runner has run, for tests
    internal int IdentityRebuildPrefolds => _identityRebuildPrefolds;

    IdentityRebuildRefusal? IIdentityRebuilder.CanRebuildIdentities
    {
        get
        {
            if (Projection.Scope != AggregationScope.SingleStream) return IdentityRebuildRefusal.NotASingleStreamProjection;
            if (!_store.SupportsIdentityRebuild) return IdentityRebuildRefusal.NotSupportedByStore;
            return null;
        }
    }

    bool IIdentityRebuilder.HasUnboundRequests => _identityRebuilds is { ReadyCount: > 0 };

    void IIdentityRebuilder.Submit(IdentityRebuildTicket ticket, long committedPosition, Func<Task> whenReady)
    {
        TId id;
        try
        {
            id = IdentityRebuildGuard.CoerceIdentity<TId>(Projection.Name, ticket.Request.Identity);
        }
        catch (Exception e)
        {
            ticket.TryFault(e);
            return;
        }

        var entry = new PendingRebuild(ticket, ticket.Request.TenantId, id);

        lock (_identityRebuildLock)
        {
            (_identityRebuilds ??= new IdentityRebuildState()).Preparing.Add(entry);
        }

        // Off the command loop: a long stream is pre-folded up to the committed position here, so the bound batch
        // only folds what arrived since. A short one is only probed, and folds inline in the bound batch.
        _ = Task.Run(async () =>
        {
            try
            {
                entry.Prefold = await prefoldAsync(entry, committedPosition, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                lock (_identityRebuildLock)
                {
                    _identityRebuilds!.Preparing.Remove(entry);
                }

                ticket.TryFault(e);
                return;
            }

            lock (_identityRebuildLock)
            {
                var state = _identityRebuilds!;

                // Abandoned while it was being prepared
                if (!state.Preparing.Remove(entry) || ticket.IsFinished) return;

                state.Ready.Add(entry);
                state.Recount();
            }

            try
            {
                await whenReady().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Could not wake the shard for a per-identity rebuild of {Identity} in {Projection}",
                    ticket.Request.Identity, Projection.Name);
            }
        });
    }

    void IIdentityRebuilder.FaultBound(Exception exception)
    {
        PendingRebuild[] bound;
        lock (_identityRebuildLock)
        {
            var state = _identityRebuilds;
            if (state == null || state.Bound.Count == 0) return;

            bound = state.Bound.ToArray();
            state.Bound.Clear();
            state.Recount();
        }

        foreach (var entry in bound) entry.Ticket.TryFault(exception);
    }

    void IIdentityRebuilder.AbandonAll()
    {
        PendingRebuild[] all;
        lock (_identityRebuildLock)
        {
            var state = _identityRebuilds;
            if (state == null) return;

            all = state.Preparing.Concat(state.Ready).Concat(state.Bound).ToArray();
            state.Preparing.Clear();
            state.Ready.Clear();
            state.Bound.Clear();
            state.Recount();
        }

        foreach (var entry in all) entry.Ticket.TryFault(entry.Ticket.Refusal(IdentityRebuildRefusal.ShardStopped));
    }

    // Called by the execution after the batch commits, from ApplyPendingCacheUpdates, so the order is: the cache
    // takes the re-fold, the callers' OnCommitted runs, the request completes, and only then is the next batch
    // built.
    private void completeBoundIdentityRebuilds()
    {
        var state = _identityRebuilds;
        if (state == null || state.Work == 0) return;

        PendingRebuild[] bound;
        lock (_identityRebuildLock)
        {
            if (state.Bound.Count == 0) return;

            bound = state.Bound.ToArray();
            state.Bound.Clear();
            state.Recount();
        }

        foreach (var entry in bound) entry.Ticket.TryComplete();
    }

    private async Task bindIdentityRebuildsAsync(IdentityRebuildState state, EventRange range,
        IProjectionBatch<TOperations, TQuerySession> batch, ShardExecutionMode mode, CancellationToken cancellation)
    {
        // A composite member writes into its parent's batch and a rebuild replays everything anyway. Neither can
        // be reached through the daemon API, so this only guards against misuse.
        if (mode == ShardExecutionMode.Rebuild || range.BatchBehavior != BatchBehavior.Individual) return;

        List<PendingRebuild> bound;
        lock (_identityRebuildLock)
        {
            // A previous build that took requests and never committed -- a skipped event made the execution build
            // the page again -- hands them to this build. Their callbacks run again against this batch.
            state.Ready.InsertRange(0, state.Bound);
            state.Bound.Clear();

            bound = state.Ready.Where(x => !x.Ticket.IsFinished).ToList();
            state.Ready.Clear();
            state.Bound.AddRange(bound);
            state.Recount();
        }

        if (bound.Count == 0) return;

        var ceiling = range.SequenceCeiling;

        // Two requests for the same identity in one batch share one re-fold, so the second completes with the
        // first's result.
        foreach (var sameIdentity in bound.GroupBy(x => (x.TenantId, x.Id)))
        {
            var entries = sameIdentity.ToList();
            var (tenantId, id) = sameIdentity.Key;

            var operations = batch.SessionForTenant(tenantId);
            var storage = await operations.FetchProjectionStorageAsync<TDoc, TId>(tenantId, cancellation)
                .ConfigureAwait(false);

            // Computed before anything for the identity is added to the batch. A re-fold that fails -- its poison
            // event is still poison -- faults the request and leaves the identity exactly as it was, page and all,
            // and the batch carries on for everything else. A failing repair never pauses the shard.
            RefoldResult refold;
            try
            {
                refold = await refoldAsync(entries[0], operations, storage, ceiling, cancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                _logger.LogWarning(e,
                    "Per-identity rebuild of {Identity} in projection {Projection} failed; the shard carries on without it",
                    id, Projection.Name);
                unbind(entries, e);
                continue;
            }

            var survivors = new List<PendingRebuild>();
            foreach (var entry in entries)
            {
                if (entry.Ticket.Request.InSameBatch is not { } inSameBatch)
                {
                    survivors.Add(entry);
                    continue;
                }

                try
                {
                    await inSameBatch(batch).ConfigureAwait(false);
                    survivors.Add(entry);
                }
                catch (Exception e)
                {
                    unbind([entry], e);
                }
            }

            if (survivors.Count == 0) continue;

            // The re-fold already holds every event of this page for the identity, so the page must not apply them
            // a second time
            foreach (var group in range.Groups.OfType<SliceGroup<TDoc, TId>>())
            {
                if (group.TenantId == tenantId) group.Slices.Remove(id);
            }

            applyRefold(refold, id, tenantId, storage);

            foreach (var entry in survivors)
            {
                entry.Ticket.BoundCeiling = ceiling;
                entry.Ticket.Action = refold.Action;
            }
        }
    }

    private void unbind(IReadOnlyList<PendingRebuild> entries, Exception exception)
    {
        lock (_identityRebuildLock)
        {
            foreach (var entry in entries) _identityRebuilds!.Bound.Remove(entry);
            _identityRebuilds!.Recount();
        }

        foreach (var entry in entries) entry.Ticket.TryFault(exception);
    }

    // The writes for a re-fold, in the order a real rebuild leaves them: the identity's existing row is cleared
    // first, hard, so a fold that ends in nothing leaves no document and a soft-deleted row does not survive a fold
    // that recreates it. A fold that deletes goes through the projection's own delete, so a soft-deleting
    // projection still soft-deletes.
    private void applyRefold(RefoldResult refold, TId id, string tenantId, IProjectionStorage<TDoc, TId> storage)
    {
        var cache = CacheFor(tenantId);

        if (refold.ArchiveStream)
        {
            storage.ArchiveStream(id, tenantId);
        }

        switch (refold.Action)
        {
            case ActionType.Delete:
                removeFromCache(cache, id);
                storage.Delete(id);
                break;

            case ActionType.Nothing:
            case ActionType.HardDelete:
                removeFromCache(cache, id);
                storage.HardDeleteById(id);
                break;

            case ActionType.Store:
                storage.HardDeleteById(id);
                storeInCache(cache, id, refold.Snapshot!);
                storage.StoreProjection(refold.Snapshot!, refold.LastEvent, Projection.Scope);
                break;

            case ActionType.UnDeleteAndStore:
                storage.HardDeleteById(id);
                storeInCache(cache, id, refold.Snapshot!);
                storage.StoreProjection(refold.Snapshot!, refold.LastEvent, Projection.Scope);
                break;

            case ActionType.StoreThenSoftDelete:
                storage.HardDeleteById(id);
                storage.StoreProjection(refold.Snapshot!, refold.LastEvent, Projection.Scope);
                removeFromCache(cache, id);
                storage.Delete(id);
                break;
        }
    }

    private async Task<RefoldResult> refoldAsync(PendingRebuild entry, TOperations operations,
        IProjectionStorage<TDoc, TId> storage, long ceiling, CancellationToken cancellation)
    {
        TDoc? snapshot = default;
        IEvent? lastPrefolded = null;
        long after = 0;
        var prefoldArchived = false;

        if (entry.Prefold is { } prefold && prefold.Position <= ceiling)
        {
            snapshot = prefold.Snapshot;
            lastPrefolded = prefold.LastEvent;
            after = prefold.Position;
            prefoldArchived = prefold.ArchiveStream;
        }

        var events = await loadStreamAsync(entry.TenantId, entry.Id, after, ceiling, Projection.Options.BatchSize,
            int.MaxValue, cancellation).ConfigureAwait(false);

        var refold = await foldAsync(entry.TenantId, entry.Id, snapshot, lastPrefolded, entry.Prefold != null, events,
            operations, storage, cancellation).ConfigureAwait(false);

        // A stream archived inside the pre-folded part is still archived. Archiving is idempotent.
        return prefoldArchived && !refold.ArchiveStream && refold.Action != ActionType.Nothing
            ? refold with { ArchiveStream = Projection.Scope == AggregationScope.SingleStream }
            : refold;
    }

    // The projection's own write path, the way ApplyChangesAsync runs it for one slice, but in rebuild terms (no
    // side effects) and without writing anything: the caller decides whether to write.
    private async Task<RefoldResult> foldAsync(string tenantId, TId id, TDoc? snapshot, IEvent? lastPrefolded,
        bool foldedAnything, IReadOnlyList<IEvent> events, TOperations operations,
        IIdentitySetter<TDoc, TId> identitySetter, CancellationToken cancellation)
    {
        var slice = new EventSlice<TDoc, TId>(id, tenantId, events) { Snapshot = snapshot };
        var group = new SliceGroup<TDoc, TId>(tenantId) { Operations = operations };
        group.Slices.Fill(id, slice);

        await Projection.EnrichEventsAsync(group, operations, cancellation).ConfigureAwait(false);

        var compactedMarker = slice.FastForwardForCompacting();
        var starting = slice.Snapshot;

        if (Projection.MatchesAnyDeleteType(slice.Events()))
        {
            return new RefoldResult(ActionType.Delete, default,
                slice.Events().LastOrDefault() ?? compactedMarker ?? lastPrefolded,
                wouldArchiveStream(slice, starting != null));
        }

        var (result, action) = await Projection.DetermineActionAsync(operations, starting, id, identitySetter,
            slice.Events(), cancellation).ConfigureAwait(false);

        if (action == ActionType.Nothing)
        {
            // Unchanged from where the fold started. A re-fold starts from nothing in storage, so an unchanged
            // pre-folded aggregate still has to be written.
            result = starting;
            if (result != null)
            {
                action = ActionType.Store;
            }
            else if (foldedAnything || slice.Events().Count > 0)
            {
                // Folded events and ended with no aggregate, so the projection deleted it along the way. The
                // projection's own delete runs, which keeps a soft-deleting projection soft-deleting. Only a stream
                // with no event the projection reads at all folds to "nothing".
                action = ActionType.Delete;
            }
        }

        IEvent? lastEvent = null;
        if (action != ActionType.Nothing)
        {
            (lastEvent, result) = Projection.TryApplyMetadata(slice.Events(), result, id, identitySetter);
        }

        var archive = wouldArchiveStream(slice, starting != null || result != null);

        // jasperfx#1214: the Compacted<T> marker is the last event of a slice that ends with it
        return new RefoldResult(action, result, lastEvent ?? compactedMarker ?? lastPrefolded, archive);
    }

    // jasperfx#1201: above BatchSize events, fold up to the committed position M off the loop and keep only the
    // snapshot, so the bound batch folds just (M, C]. Below it, return nothing and fold inline.
    private async Task<Prefolded?> prefoldAsync(PendingRebuild entry, long committedPosition,
        CancellationToken cancellation)
    {
        if (committedPosition <= 0) return null;

        var batchSize = Math.Max(Projection.Options.BatchSize, 1);

        var probe = await loadStreamAsync(entry.TenantId, entry.Id, 0, committedPosition, batchSize + 1, batchSize + 1,
            cancellation).ConfigureAwait(false);

        if (probe.Count <= batchSize) return null;

        var session = _store.OpenSession(_database, entry.TenantId);
        try
        {
            var setter = new DetachedIdentitySetter();
            TDoc? snapshot = default;
            IEvent? last = null;
            var archived = false;
            IReadOnlyList<IEvent> page = probe;

            while (page.Count > 0)
            {
                var folded = await foldAsync(entry.TenantId, entry.Id, snapshot, last, true, page, session, setter,
                    cancellation).ConfigureAwait(false);

                snapshot = folded.Action is ActionType.Delete or ActionType.HardDelete or ActionType.Nothing
                    ? default
                    : folded.Snapshot;
                last = folded.LastEvent;
                archived |= folded.ArchiveStream;

                if (page.Count < batchSize) break;

                page = await loadStreamAsync(entry.TenantId, entry.Id, page[^1].Sequence, committedPosition, batchSize,
                    batchSize, cancellation).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _identityRebuildPrefolds);
            return new Prefolded(snapshot, committedPosition, last, archived);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Pages through the store's re-fold loader, at most `maximum` events in all
    private async Task<IReadOnlyList<IEvent>> loadStreamAsync(string tenantId, TId id, long after, long ceiling,
        int pageSize, int maximum, CancellationToken cancellation)
    {
        var filtering = Projection as EventFilterable;
        var eventTypes = filtering?.IncludedEventTypes.ToArray() ?? Projection.AllEventTypes;

        var events = new List<IEvent>();
        var floor = after;
        while (events.Count < maximum)
        {
            var limit = Math.Min(pageSize, maximum - events.Count);
            var request = new IdentityRebuildEventRequest(tenantId, id, floor, ceiling, limit)
            {
                EventTypes = eventTypes,
                IncludeArchivedEvents = filtering?.IncludeArchivedEvents ?? false
            };

            var page = await _store.LoadEventsForIdentityRebuildAsync(_database, request, cancellation)
                .ConfigureAwait(false);

            events.AddRange(page);
            if (page.Count < limit) break;

            floor = page[^1].Sequence;
        }

        return events;
    }

    private sealed class DetachedIdentitySetter : IIdentitySetter<TDoc, TId>
    {
        // The bound batch sets the identity again through the real storage
        public void SetIdentity(TDoc document, TId identity)
        {
        }

        public TId Identity(TDoc document)
            => throw new NotSupportedException("A pre-fold has no storage to read an identity from.");
    }

    private sealed record RefoldResult(ActionType Action, TDoc? Snapshot, IEvent? LastEvent, bool ArchiveStream);

    private sealed record Prefolded(TDoc? Snapshot, long Position, IEvent? LastEvent, bool ArchiveStream);

    private sealed class PendingRebuild(IdentityRebuildTicket ticket, string tenantId, TId id)
    {
        public IdentityRebuildTicket Ticket { get; } = ticket;
        public string TenantId { get; } = tenantId;
        public TId Id { get; } = id;
        public Prefolded? Prefold { get; set; }
    }

    private sealed class IdentityRebuildState
    {
        public readonly List<PendingRebuild> Preparing = new();
        public readonly List<PendingRebuild> Ready = new();
        public readonly List<PendingRebuild> Bound = new();

        private volatile int _ready;
        private volatile int _work;

        public int ReadyCount => _ready;

        // Ready plus bound: BuildBatchAsync and the post-commit completion skip everything when it is zero
        public int Work => _work;

        // Only under the runner's lock
        public void Recount()
        {
            _ready = Ready.Count;
            _work = Ready.Count + Bound.Count;
        }
    }
}
