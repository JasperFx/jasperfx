using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

// marten#6003: per-identity rebuild, replay and discard of a multi-stream identity.
//
// The events of identity X are read in sequence order from the projection's identity source and sliced the way the
// daemon sliced them (IdentityEvents on the projection). The bound batch reads them up to its own floor F, which the
// shard committed before it built this batch, and takes X's slice of its page (F, C] from the page's own grouping. Then
// it is #1201: the batch writes X's document, takes X out of the page, unparks X if it was parked, and commits with the
// shard's progression.
//
// Two folds:
// - from nothing (RebuildIdentityAsync, and a scan): every event of X up to C, pre-folded off the loop up to the
//   committed position when there are many. A parked identity is folded silently up to its resume point and with side
//   effects after it, as a single-stream one is.
// - the resume fold (a replay or discard of a parked identity): X's stored document holds exactly X's events up to the
//   resume point, so the fold starts from it and folds the events after it, all of which owe their side effects.
public partial class AggregationRunner<TDoc, TId, TOperations, TQuerySession>
{
    // Why this multi-stream identity can't be rebuilt the way it was asked, or null when it can
    private async Task<IdentityRebuildRefusedException?> refuseIdentitySourceAsync(PendingRebuild entry)
    {
        var plan = identitySources;
        var ticket = entry.Ticket;
        if (plan == null) return ticket.Refusal(IdentityRebuildRefusal.NotASingleStreamProjection);

        if (entry.Scan)
        {
            return _store.SupportsIdentityEventSource(IdentityEventSource.Scan)
                ? null
                : ticket.Refusal(IdentityRebuildRefusal.NotSupportedByStore,
                    "The event store can't scan a projection's events for one identity.");
        }

        if (!plan.Access.Exact)
        {
            return ticket.Refusal(IdentityRebuildRefusal.IdentityNotFindable, plan.Access.Reason);
        }

        var missing = plan.Sources.Where(x => !_store.SupportsIdentityEventSource(x)).ToArray();
        if (missing.Length > 0)
        {
            return ticket.Refusal(IdentityRebuildRefusal.NotSupportedByStore,
                $"The event store can't read one identity's events from the {string.Join(", ", missing)} source.");
        }

        // A resume fold only needs the rows after the resume point, which every park has. A rebuild from nothing needs
        // the whole index.
        if (plan.SliceIndex && !entry.Resume)
        {
            var shard = _parkingShard ?? ShardName.Compose(Projection.Name, version: identityProjection!.Version);
            var start = await sliceIndexStartAsync(shard, CancellationToken.None).ConfigureAwait(false);
            if (start is not 0)
            {
                return ticket.Refusal(IdentityRebuildRefusal.SliceIndexIncomplete,
                    start.HasValue
                        ? $"The slice index starts at sequence {start}."
                        : "The slice index has not started yet.");
            }
        }

        return null;
    }

    // Off the loop: fold the identity's events up to the committed position. A scan always does this, with progress,
    // so the bound batch only scans what arrived since. Otherwise only an identity with more events than one batch.
    private async Task<Prefolded?> prefoldIdentityAsync(PendingRebuild entry, long committedPosition,
        CancellationToken cancellation)
    {
        if (committedPosition <= 0) return null;

        var batchSize = Math.Max(Projection.Options.BatchSize, 1);
        var session = _store.OpenSession(_database, entry.TenantId);
        try
        {
            var setter = new DetachedIdentitySetter();
            TDoc? snapshot = default;
            IEvent? last = null;
            var found = 0;
            var after = 0L;
            var first = true;

            while (true)
            {
                var chunk = await identityProjection!.ReadIdentityChunkAsync(_store, _database, session,
                    entry.TenantId, entry.Id, after, committedPosition, batchSize, false, entry.Scan, cancellation)
                    .ConfigureAwait(false);

                // A short identity folds inline in the bound batch
                if (first && !entry.Scan && !chunk.More && chunk.Sliced.Count <= batchSize) return null;
                first = false;

                if (chunk.Sliced.Count > 0)
                {
                    var folded = await foldAsync(entry.TenantId, entry.Id, snapshot, last, true, chunk.Sliced,
                        session, setter, cancellation).ConfigureAwait(false);

                    snapshot = folded.Action is ActionType.Delete or ActionType.HardDelete or ActionType.Nothing
                        ? default
                        : folded.Snapshot;
                    last = folded.LastEvent;
                    found += chunk.Stored.Count;
                }

                entry.Ticket.Request.ScanProgress?.Report(new IdentityScanProgress(
                    chunk.More ? chunk.NextAfter : committedPosition, committedPosition, found));

                if (!chunk.More) break;
                after = chunk.NextAfter;
            }

            Interlocked.Increment(ref _identityRebuildPrefolds);
            return new Prefolded(snapshot, committedPosition, last, false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // In the bound batch, floor F, the identity's slice of the page in pageEvents
    private async Task<RefoldResult> refoldIdentityAsync(PendingRebuild entry, TOperations operations,
        IProjectionStorage<TDoc, TId> storage, long floor, IReadOnlyList<IEvent> pageEvents, ParkedIdentity? parked,
        bool skipFailing, CancellationToken cancellation)
    {
        long after;
        long? silentThrough;
        Func<Task<(TDoc? Snapshot, IEvent? Last, bool FoldedAnything)>> start;

        if (entry.Resume && parked != null)
        {
            // The stored document is exactly the identity's events up to the resume point. Read again for every
            // attempt, because a fold that failed may have changed the instance it was given.
            after = parked.ResumeAfter;
            silentThrough = null;
            start = async () =>
            {
                var stored = await storage.LoadManyAsync([entry.Id], cancellation).ConfigureAwait(false);
                return stored.TryGetValue(entry.Id, out var document)
                    ? (document, null, true)
                    : (default(TDoc), null, false);
            };
        }
        else if (!skipFailing && entry.Prefold is { } prefold && prefold.Position <= floor &&
                 (parked == null || prefold.Position <= parked.ResumeAfter))
        {
            after = prefold.Position;
            silentThrough = parked?.ResumeAfter;
            start = () => Task.FromResult((prefold.Snapshot, prefold.LastEvent, true));
        }
        else
        {
            after = 0;
            silentThrough = parked?.ResumeAfter;
            start = () => Task.FromResult((default(TDoc), (IEvent?)null, false));
        }

        // Up to the batch's floor from the identity source, then the page's own slice of the identity
        var events = new List<IEvent>();
        if (after < floor)
        {
            while (true)
            {
                var chunk = await identityProjection!.ReadIdentityChunkAsync(_store, _database, operations,
                    entry.TenantId, entry.Id, after, floor, Math.Max(Projection.Options.BatchSize, 1), skipFailing,
                    entry.Scan, cancellation).ConfigureAwait(false);

                events.AddRange(chunk.Sliced);
                if (!chunk.More) break;

                after = chunk.NextAfter;
            }
        }

        events.AddRange(pageEvents);

        var skipped = new List<ApplyEventException>();
        while (true)
        {
            var folding = skipped.Count == 0
                ? events
                : events.Where(e => skipped.All(x => x.Event.Sequence != e.Sequence)).ToList();

            var (snapshot, last, foldedAnything) = await start().ConfigureAwait(false);

            RefoldResult refold;
            try
            {
                if (entry.Resume)
                {
                    refold = await foldAsync(entry.TenantId, entry.Id, snapshot, last, foldedAnything, folding,
                        operations, storage, cancellation).ConfigureAwait(false);

                    // Every event of a resume fold is above the resume point, so all of them owe side effects
                    refold = refold with { SideEffectSlice = refold.Slice };
                }
                else
                {
                    refold = await foldSplitAsync(entry, snapshot, last, foldedAnything, folding, silentThrough,
                        operations, storage, cancellation).ConfigureAwait(false);
                }
            }
            catch (ApplyEventException e) when (skipFailing &&
                                               !ProjectionExceptions.IsSystemicDatabaseFailure(e) &&
                                               skipped.All(x => x.Event.Sequence != e.Event.Sequence))
            {
                skipped.Add(e);
                continue;
            }

            return skipped.Count == 0 ? refold : refold with { Skipped = skipped };
        }
    }
}
