using System.Collections.Concurrent;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

// marten#6003: park the failing slice. A slice whose fold throws is dropped from the batch and a parked row for its
// identity rides the batch that moves the shard's progression. Later batches drop the identity's slices until a
// per-identity rebuild (jasperfx#1201) re-folds it and unparks it in the same batch.
//
// Phase 1 parks single-stream aggregations only. A multi-stream projection is classified (IdentityAccess) but falls
// back to Pause: parking it needs the resume fold over an exact identity source, which is the seam marked "phase 2"
// below.
public partial class AggregationRunner<TDoc, TId, TOperations, TQuerySession> : IParkedIdentityHost
{
    // Null until the shard has parked something, or loaded a parked row at start. The whole of the idle cost is the
    // read of this field and the count beside it, once per batch.
    private ImHashMap<(string TenantId, TId Id), ParkedIdentity>? _parked;
    private volatile int _parkedCount;
    private readonly Lock _parkedLock = new();

    private bool _parkedLoaded;
    private bool _loggedParkingFallback;
    private ShardName? _parkingShard;
    private ISubscriptionAgent? _parkingAgent;

    // Per build. A build that never commits leaves these behind and the next build starts over, the same way it drops
    // its cache mutations (#4730).
    private bool _parkingThisBuild;
    private ConcurrentQueue<PendingPark> _pendingParks = new();
    private List<ParkedIdentity>? _parksToPromote;
    private List<(ParkedIdentity Row, string Outcome)>? _unparksToPromote;

    private static Func<IEvent, TId>? _streamIdentitySource;

    /// <summary>How many identities this runner holds parked, or null before it has loaded or parked any.</summary>
    int? IParkedIdentityHost.ParkedCount => _parkedLoaded || _parked != null ? _parkedCount : null;

    // Phase 1: single-stream aggregations only. Phase 2 widens this to multi-stream projections whose IdentityAccess
    // is exact (IdentityFromStream / IdentityFromTag / the slice index), with the resume fold over that source.
    private bool projectionCanPark => Projection.Scope == AggregationScope.SingleStream;

    bool IParkedIdentityHost.ParksUnreadableEvents(ErrorHandlingOptions options)
        => projectionCanPark && _store.SupportsParkedIdentities &&
           options.SerializationErrors == ProjectionErrorAction.Park;

    bool IParkedIdentityHost.TryFindParked(string tenantId, object identity, out ParkedIdentity parked)
    {
        parked = null!;
        if (!tryCoerce(identity, out var id)) return false;

        var found = tryFindParked(tenantId, id);
        if (found == null) return false;

        parked = found;
        return true;
    }

    IReadOnlyList<ParkedIdentity> IParkedIdentityHost.ParkedSnapshot()
    {
        var parked = _parked;
        if (parked == null) return [];

        return parked.Enumerate().Select(x => x.Value).OrderBy(x => x.ParkedAt).ThenBy(x => x.FirstFailingSequence)
            .ToList();
    }

    async Task<IReadOnlyList<ParkedIdentity>> IParkedIdentityHost.LoadRequestedActionsAsync(CancellationToken token)
    {
        var shard = _parkingShard;
        if (_parkedCount == 0 || shard == null) return [];

        var rows = await _database.LoadRequestedParkedActionsAsync(shard, token).ConfigureAwait(false);

        // Only what this runner holds: a row it doesn't know about is not one it can unpark
        var limit = Math.Max(_store.ErrorHandlingOptions(ShardExecutionMode.Continuous).ParkedReplayBatchSize, 1);
        return rows
            .Where(x => x.RequestedAction.HasValue)
            .Select(x => tryCoerce(x.Identity, out var id) || tryParse(x.IdentityText, out id)
                ? tryFindParked(x.TenantId, id) is { } held ? held with { RequestedAction = x.RequestedAction } : null
                : null)
            .OfType<ParkedIdentity>()
            .Take(limit)
            .ToList();
    }

    private ParkedIdentity? tryFindParked(string tenantId, TId id)
    {
        var parked = _parked;
        return parked != null && parked.TryFind((tenantId, id), out var row) ? row : null;
    }

    private bool tryCoerce(object identity, out TId id)
    {
        if (identity is TId typed)
        {
            id = typed;
            return true;
        }

        if (identity is string text && typeof(TId) != typeof(string)) return tryParse(text, out id);

        id = default!;
        return false;
    }

    private static bool tryParse(string text, out TId id)
    {
        try
        {
            id = ParkedIdentityText.Parse<TId>(text);
            return true;
        }
        catch (Exception)
        {
            id = default!;
            return false;
        }
    }

    private static Func<IEvent, TId> streamIdentitySource
        => _streamIdentitySource ??= IEvent.CreateAggregateIdentitySource<TId>();

    // At the top of BuildBatchAsync. Decides whether this build parks, and the first time a continuous shard builds,
    // loads the rows it parked before (here or on another node).
    private async Task prepareParkingAsync(EventRange range, ShardExecutionMode mode, CancellationToken cancellation)
    {
        _pendingParks = new ConcurrentQueue<PendingPark>();
        _parksToPromote = null;
        _unparksToPromote = null;

        var individualContinuous = mode == ShardExecutionMode.Continuous &&
                                   range.BatchBehavior == BatchBehavior.Individual;

        _parkingThisBuild = individualContinuous && projectionCanPark && _store.SupportsParkedIdentities &&
                            _store.ErrorHandlingOptions(ShardExecutionMode.Continuous).ApplyErrors ==
                            ProjectionErrorAction.Park;

        if (!individualContinuous || !_store.SupportsParkedIdentities) return;

        // Said once per shard start, so a user who asked for parking can see why this projection pauses instead
        if (!_loggedParkingFallback && !projectionCanPark &&
            _store.ErrorHandlingOptions(ShardExecutionMode.Continuous).ApplyErrors == ProjectionErrorAction.Park)
        {
            var access = (Projection as IHasIdentityAccess)?.IdentityAccess;
            _logger.LogInformation(
                "Projection {Projection} does not park failing identities ({Access}); a failing event pauses shard {Shard} instead",
                Projection.Name, access?.ToString() ?? "not identity-sliced", range.ShardName.Identity);
            _loggedParkingFallback = true;
        }

        _parkingAgent = range.Agent;
        _parkingShard ??= range.ShardName;

        if (_parkedLoaded || !projectionCanPark) return;

        // Once per shard start: the runner, like its execution, lives as long as the agent does
        var rows = await _database.LoadParkedIdentitiesAsync(range.ShardName, cancellation).ConfigureAwait(false);

        lock (_parkedLock)
        {
            if (_parkedLoaded) return;

            var map = _parked ?? ImHashMap<(string TenantId, TId Id), ParkedIdentity>.Empty;
            foreach (var row in rows)
            {
                if (!tryCoerce(row.Identity, out var id) && !tryParse(row.IdentityText, out id))
                {
                    _logger.LogWarning(
                        "Ignoring parked row {Identity} of {Shard}: it is not an identity of type {IdType}",
                        row.IdentityText, range.ShardName.Identity, typeof(TId).FullNameInCode());
                    continue;
                }

                map = map.AddOrUpdate((row.TenantId, id), row with { Identity = id });
            }

            _parked = map;
            _parkedCount = map.Count();
            _parkedLoaded = true;
        }

        if (_parkedCount > 0)
        {
            _logger.LogInformation("Shard {Shard} starts with {Count} parked identities", range.ShardName.Identity,
                _parkedCount);
            range.Agent.Metrics.UpdateParkedCount(_parkedCount);
        }
    }

    // After the per-identity rebuilds are bound. Unreadable events become parks, and every slice of an identity that
    // is still parked is dropped from the page.
    private void applyParkedFilter(EventRange range)
    {
        if (_parkingThisBuild && range.Events is EventPage { Unreadable.Count: > 0 } page)
        {
            parkUnreadableEvents(range, page);
        }
        else if (range.Events is EventPage { Unreadable.Count: > 0 })
        {
            // The agent only asks the loader for these when this runner parks them
            throw new InvalidOperationException(
                $"Shard {range.ShardName.Identity} was handed unreadable events but does not park them");
        }

        var parked = _parked;
        if (parked == null || _parkedCount == 0) return;

        var held = 0;
        foreach (var group in range.Groups.OfType<SliceGroup<TDoc, TId>>())
        {
            foreach (var slice in group.Slices.ToArray())
            {
                if (!parked.TryFind((group.TenantId, slice.Id), out _)) continue;

                held += slice.Events().Count;
                group.Slices.Remove(slice.Id);
            }
        }

        if (held > 0)
        {
            range.Agent.Metrics.IncrementHeldEvents(held);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Shard {Shard} held back {Count} events of parked identities in {Range}",
                    range.ShardName.Identity, held, range);
            }
        }
    }

    private void parkUnreadableEvents(EventRange range, EventPage page)
    {
        var defaultTenant = range.ShardName.TenantId ?? StorageConstants.DefaultTenantId;

        foreach (var unreadable in page.Unreadable)
        {
            var stub = new Event<UnreadableEventStub>(new UnreadableEventStub())
            {
                Sequence = unreadable.Sequence,
                StreamId = unreadable.StreamId ?? Guid.Empty,
                StreamKey = unreadable.StreamKey,
                TenantId = unreadable.TenantId ?? defaultTenant
            };

            TId id;
            try
            {
                id = streamIdentitySource(stub);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Could not tell which stream the unreadable event #{unreadable.Sequence} belongs to, so it cannot be parked: {e.Message}",
                    unreadable.Exception);
            }

            var tenantId = stub.TenantId;

            // Already parked, or parked by an earlier unreadable event of this page: it is held like any other event
            if (tryFindParked(tenantId, id) != null ||
                _pendingParks.Any(x => x.TenantId == tenantId && x.Id.Equals(id)))
            {
                continue;
            }

            foreach (var group in range.Groups.OfType<SliceGroup<TDoc, TId>>())
            {
                if (group.TenantId == tenantId) group.Slices.Remove(id);
            }

            _pendingParks.Enqueue(new PendingPark(tenantId, id, unreadable.Sequence,
                ShardFailureCategory.EventSerialization, unreadable.Exception, unreadable.EventTypeName, null,
                unreadable.StreamId is { } guid && guid != Guid.Empty ? guid.ToString("D") : unreadable.StreamKey,
                null));
        }
    }

    // From ApplyChangesAsync, when the fold of one slice threw and this build parks
    private void parkFailedSlice(EventSlice<TDoc, TId> slice, IAggregateCache<TId, TDoc> cache, ApplyEventException e)
    {
        var @event = e.Event;
        var stream = @event.StreamId != Guid.Empty ? @event.StreamId.ToString("D") : @event.StreamKey;

        _pendingParks.Enqueue(new PendingPark(slice.TenantId, slice.Id, @event.Sequence,
            ShardFailureCategory.ApplyEvent, e.InnerException ?? e, @event.EventTypeName, @event.Id, stream, e));

        // The fold may have mutated a cached aggregate before it threw. The removal is deferred to the commit like
        // every other cache mutation (#4730).
        removeFromCache(cache, slice.Id);
    }

    // After the slices ran, before the batch is handed back. The cap and the trip are checked here, so a batch that
    // would pass either one never commits.
    private void writePendingParks(EventRange range, IProjectionBatch batch, int identitiesInBatch)
    {
        if (!_parkingThisBuild || _pendingParks.IsEmpty) return;

        var pending = _pendingParks.ToArray();
        var options = _store.ErrorHandlingOptions(ShardExecutionMode.Continuous);
        var firstApplyFailure = pending.Select(x => x.ApplyFailure).FirstOrDefault(x => x != null);

        if (pending.Length >= options.ParkedTripMinimum &&
            pending.Length > identitiesInBatch * options.ParkedTripFraction)
        {
            throw new ParkedLimitReachedException(range.ShardName,
                $"{pending.Length} of the {identitiesInBatch} identities in the batch from {range.SequenceFloor} to {range.SequenceCeiling} failed, which looks like a fault in the projection rather than in the data",
                firstApplyFailure);
        }

        if (_parkedCount + pending.Length > options.MaxParkedIdentitiesPerShard)
        {
            throw new ParkedLimitReachedException(range.ShardName,
                $"parking {pending.Length} more identities would pass the limit of {options.MaxParkedIdentitiesPerShard} (it holds {_parkedCount})",
                firstApplyFailure);
        }

        var now = _store.TimeProvider.GetUtcNow();
        var rows = new List<ParkedIdentity>(pending.Length);
        foreach (var park in pending)
        {
            var row = new ParkedIdentity
            {
                ProjectionName = range.ShardName.Name,
                ProjectionVersion = range.ShardName.Version,
                ShardKey = range.ShardName.ShardKey,
                TenantId = park.TenantId,
                Identity = park.Id,
                IdentityText = ParkedIdentityText.Format(park.Id),
                IdentityType = ParkedIdentityText.TypeNameOf(park.Id),
                FirstFailingSequence = park.Sequence,

                // Every event of the identity at or below the floor was committed by earlier batches, and this batch
                // drops the identity's whole slice, so the document holds exactly the events up to here
                ResumeAfter = range.SequenceFloor,
                Category = park.Category,
                ExceptionType = park.Exception.GetType().FullNameInCode(),
                ExceptionMessage = park.Exception.Message,
                EventTypeName = park.EventTypeName,
                EventId = park.EventId,
                FailingStream = park.FailingStream,
                ParkedAt = now
            };

            batch.ParkIdentity(row);
            rows.Add(row);

            _logger.LogWarning(park.Exception,
                "Shard {Shard} parked {Identity} (tenant {TenantId}) after event #{Sequence} failed; its later events are held until it is replayed or discarded",
                range.ShardName.Identity, row.IdentityText, row.TenantId, park.Sequence);
        }

        _parksToPromote = rows;
    }

    // From ApplyPendingCacheUpdates: the batch committed
    private void promoteParkingChanges()
    {
        var parks = _parksToPromote;
        var unparks = _unparksToPromote;
        _parksToPromote = null;
        _unparksToPromote = null;

        if (parks == null && unparks == null) return;

        lock (_parkedLock)
        {
            var map = _parked ?? ImHashMap<(string TenantId, TId Id), ParkedIdentity>.Empty;

            foreach (var row in parks ?? [])
            {
                map = map.AddOrUpdate((row.TenantId, (TId)row.Identity), row);
            }

            foreach (var (row, _) in unparks ?? [])
            {
                map = map.Remove((row.TenantId, (TId)row.Identity));
            }

            _parked = map;
            _parkedCount = map.Count();
        }

        var agent = _parkingAgent;
        if (agent == null) return;

        if (parks is { Count: > 0 })
        {
            agent.Metrics.IncrementParked(parks.Count);
        }

        foreach (var (_, outcome) in unparks ?? [])
        {
            agent.Metrics.IncrementUnparked(outcome);
        }

        agent.Metrics.UpdateParkedCount(_parkedCount);

        if (agent is SubscriptionAgent subscriptionAgent)
        {
            subscriptionAgent.ParkedSetChanged(parks is { Count: > 0 } ? ShardAction.Parked : ShardAction.Unparked);
        }
    }

    // A replay or discard of a parked identity failed: keep the row, count the attempt and say why. The in-memory row
    // changes now, the stored one out of band and best effort.
    private void recordReplayFailure(ParkedIdentity row, Exception exception)
    {
        var now = _store.TimeProvider.GetUtcNow();
        var message = (exception is ApplyEventException { InnerException: { } inner } ? inner : exception).Message;

        lock (_parkedLock)
        {
            var map = _parked;
            var key = (row.TenantId, (TId)row.Identity);
            if (map != null && map.TryFind(key, out var current))
            {
                _parked = map.AddOrUpdate(key, current with
                {
                    ReplayAttempts = current.ReplayAttempts + 1,
                    LastAttemptAt = now,
                    LastAttemptError = message,
                    RequestedAction = null,
                    RequestedAt = null
                });
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _database.RecordParkedReplayFailureAsync(row.Key, now, message, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Could not record the failed replay of parked identity {Identity} of {Projection}",
                    row.IdentityText, row.ProjectionName);
            }
        });
    }

    private sealed class UnreadableEventStub;

    private sealed record PendingPark(
        string TenantId,
        TId Id,
        long Sequence,
        ShardFailureCategory Category,
        Exception Exception,
        string? EventTypeName,
        Guid? EventId,
        string? FailingStream,
        ApplyEventException? ApplyFailure);
}

/// <summary>
/// marten#6003: the parking half of an <see cref="AggregationRunner{TDoc,TId,TOperations,TQuerySession}" />, as the
/// agent and the daemon see it.
/// </summary>
internal interface IParkedIdentityHost
{
    /// <summary>How many identities the shard holds parked, or null before it has loaded or parked any.</summary>
    int? ParkedCount { get; }

    /// <summary>Should the agent ask its loader to report unreadable events instead of throwing?</summary>
    bool ParksUnreadableEvents(ErrorHandlingOptions options);

    /// <summary>Find a parked identity. The identity may be the projection's own identity type, or its text.</summary>
    bool TryFindParked(string tenantId, object identity, out ParkedIdentity parked);

    /// <summary>Every parked identity, oldest first.</summary>
    IReadOnlyList<ParkedIdentity> ParkedSnapshot();

    /// <summary>The durable operator requests for identities this shard holds, at most one replay batch of them.</summary>
    Task<IReadOnlyList<ParkedIdentity>> LoadRequestedActionsAsync(CancellationToken token);
}
