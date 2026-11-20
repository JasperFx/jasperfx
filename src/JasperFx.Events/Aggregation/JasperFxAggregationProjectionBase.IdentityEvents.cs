using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

// marten#6003: finding one identity's events. A single-stream projection reads its stream. A multi-stream projection
// reads candidates from its identity source (its streams, its DCB tags, its slice index, or a scan) and runs its own
// slicer over them to keep the identity's, unless a slice index is replayed "as recorded", in which case the indexed
// events are the identity's.
public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
    : IIdentityEventReader<TOperations, TQuerySession>
{
    /// <summary>How this projection finds one identity's events.</summary>
    internal virtual IdentitySourcePlan IdentitySources => Scope == AggregationScope.SingleStream
        ? IdentitySourcePlan.ForStream(IdentityAccess)
        : IdentitySourcePlan.ScanOnly(IdentityAccess);

    // marten#6003: the grouping a multi-stream projection gives one identity of these candidates. The default (single
    // stream) takes every candidate, since the candidates are the stream.
    internal virtual ValueTask<EventSlice<TDoc, TId>?> SliceForIdentityAsync(TQuerySession session, string tenantId,
        TId id, IReadOnlyList<IEvent> candidates, bool asRecorded)
        => new(new EventSlice<TDoc, TId>(id, tenantId, candidates));

    private IReadOnlyList<Type> identityEventTypes() => IncludedEventTypes.ToArray();

    /// <summary>
    /// One chunk of an identity's events: the candidates one read gives, sliced. Paged by the candidates, so a chunk
    /// may hold none of the identity's events and still have more after it.
    /// </summary>
    internal async Task<IdentityEventChunk> ReadIdentityChunkAsync(IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, TQuerySession session, string tenantId, TId id, long after, long ceiling, int limit,
        bool skipSerializationErrors, bool scan, CancellationToken token)
    {
        var plan = IdentitySources;
        limit = Math.Max(limit, 1);

        var reads = new List<IdentityEventLoadRequest>();
        IdentityEventLoadRequest request(IdentityEventSource source, string? tenant, object? identity,
            Type? tagType = null)
            => new(source, tenant, identity, after, ceiling, limit)
            {
                TagType = tagType,
                EventTypes = identityEventTypes(),
                IncludeArchivedEvents = IncludeArchivedEvents,
                SkipSerializationErrors = skipSerializationErrors,
                IncludeTags = IncludeEventTags || tagType != null,
                ProjectionName = Name,
                ProjectionVersion = Version
            };

        if (scan || !plan.Access.Exact)
        {
            reads.Add(request(IdentityEventSource.Scan, plan.AcrossTenants ? null : tenantId, null));
        }
        else
        {
            if (plan.Stream) reads.Add(request(IdentityEventSource.Stream, tenantId, id));
            foreach (var tagType in plan.TagTypes)
            {
                reads.Add(request(IdentityEventSource.Tag, tenantId, id, tagType));
            }

            if (plan.SliceIndex) reads.Add(request(IdentityEventSource.SliceIndex, tenantId, id));
        }

        // Union by sequence. A source that filled its page stops the chunk at its last event, so no source skips ahead
        // of another.
        var bySequence = new SortedDictionary<long, IEvent>();
        long? stopAt = null;
        foreach (var read in reads)
        {
            var events = await store.LoadEventsForIdentityAsync(database, read, token).ConfigureAwait(false);
            foreach (var @event in events) bySequence.TryAdd(@event.Sequence, @event);

            if (events.Count >= limit)
            {
                var last = events[^1].Sequence;
                stopAt = stopAt.HasValue ? Math.Min(stopAt.Value, last) : last;
            }
        }

        var candidates = stopAt is { } stop
            ? bySequence.Values.Where(x => x.Sequence <= stop).ToList()
            : bySequence.Values.ToList();

        var slice = candidates.Count == 0
            ? null
            : await SliceForIdentityAsync(session, tenantId, id, candidates, plan.SliceIndex && plan.AsRecorded)
                .ConfigureAwait(false);

        var sliced = slice?.Events() ?? [];
        IReadOnlyList<IEvent> stored;
        if (sliced.Count == 0)
        {
            stored = [];
        }
        else if (ReferenceEquals(sliced, candidates) || sliced.Count == candidates.Count &&
                 sliced.Select(x => x.Sequence).SequenceEqual(candidates.Select(x => x.Sequence)))
        {
            stored = candidates;
        }
        else
        {
            var sequences = sliced.Select(x => x.Sequence).ToHashSet();
            stored = candidates.Where(x => sequences.Contains(x.Sequence)).ToList();
        }

        return new IdentityEventChunk(sliced.ToList(), stored, candidates.Count, stopAt ?? ceiling, stopAt.HasValue);
    }

    async Task<IdentityEventPage> IIdentityEventReader<TOperations, TQuerySession>.LoadIdentityEventsAsync(
        IEventStore<TOperations, TQuerySession> store, IEventDatabase database, IdentityEventsRequest request,
        CancellationToken token)
    {
        var plan = IdentitySources;
        var tenantId = request.TenantId ?? StorageConstants.DefaultTenantId;
        var id = coerceIdentity(request.Identity);
        var ceiling = request.Ceiling ?? long.MaxValue;
        var limit = Math.Max(request.Limit, 1);

        var scan = !plan.Access.Exact;
        if (scan && request.ScanWindow is not > 0)
        {
            throw new InvalidOperationException(
                $"Projection '{Name}' can only find one identity's events by scanning ({plan.Access.Reason}). Pass a ScanWindow to say how many events to read.");
        }

        var source = scan
            ? IdentityEventSource.Scan
            : plan.Access.Source ?? IdentityEventSource.Stream;

        var exact = !scan;
        if (plan.SliceIndex && !scan)
        {
            var mark = await database.FetchEventRangeMarkAsync(SliceIndexMarks.KeyFor(Name, Version), null, token)
                .ConfigureAwait(false);

            // Every row above the mark is there; below it the index may be missing rows
            exact = mark is { } start && request.AfterSequence >= start;
        }

        if (!store.SupportsIdentityEventSource(source))
        {
            throw new NotSupportedException(
                $"{store.GetType().FullNameInCode()} can't read one identity's events from the {source} source that projection '{Name}' needs.");
        }

        var session = store.OpenSession(database, tenantId);
        try
        {
            if (scan)
            {
                var window = (int)Math.Min(request.ScanWindow!.Value, int.MaxValue);
                var chunk = await ReadIdentityChunkAsync(store, database, session, tenantId, id, request.AfterSequence,
                    ceiling, window, false, true, token).ConfigureAwait(false);

                return new IdentityEventPage(chunk.Stored, IdentityEventSource.Scan, false, chunk.NextAfter,
                    chunk.More);
            }

            var events = new List<IEvent>();
            var after = request.AfterSequence;
            while (true)
            {
                var chunk = await ReadIdentityChunkAsync(store, database, session, tenantId, id, after, ceiling, limit,
                    false, false, token).ConfigureAwait(false);

                foreach (var @event in chunk.Stored)
                {
                    events.Add(@event);
                    if (events.Count == limit)
                    {
                        // A full page: the next one starts after this event
                        return new IdentityEventPage(events, source, exact, @event.Sequence, true);
                    }
                }

                if (!chunk.More) return new IdentityEventPage(events, source, exact, chunk.NextAfter, false);

                after = chunk.NextAfter;
            }
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private TId coerceIdentity(object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (identity is TId id) return id;
        if (identity is string text) return ParkedIdentityText.Parse<TId>(text);

        throw new ArgumentOutOfRangeException(nameof(identity),
            $"Projection '{Name}' is keyed by {typeof(TId).FullNameInCode()}, not {identity.GetType().FullNameInCode()}.");
    }
}

/// <summary>
/// marten#6003: how a projection finds one identity's events: the sources it reads candidates from, and whether a
/// slice index is the grouping (as recorded) or is sliced again.
/// </summary>
internal sealed record IdentitySourcePlan(
    IdentityAccess Access,
    bool Stream,
    IReadOnlyList<Type> TagTypes,
    bool SliceIndex,
    bool AsRecorded,
    bool AcrossTenants)
{
    public static IdentitySourcePlan ForStream(IdentityAccess access) => new(access, true, [], false, false, false);

    public static IdentitySourcePlan ScanOnly(IdentityAccess access) => new(access, false, [], false, false, false);

    /// <summary>The sources a store has to read for this plan.</summary>
    public IEnumerable<IdentityEventSource> Sources
    {
        get
        {
            if (!Access.Exact)
            {
                yield return IdentityEventSource.Scan;
                yield break;
            }

            if (Stream) yield return IdentityEventSource.Stream;
            if (TagTypes.Count > 0) yield return IdentityEventSource.Tag;
            if (SliceIndex) yield return IdentityEventSource.SliceIndex;
        }
    }
}

/// <summary>
/// marten#6003: one read of an identity's candidates, sliced.
/// </summary>
/// <param name="Sliced">The identity's slice of the candidates, fan-out applied: what a fold applies.</param>
/// <param name="Stored">The stored events behind <paramref name="Sliced" />, in sequence order.</param>
/// <param name="CandidateCount">How many candidates the read gave.</param>
/// <param name="NextAfter">Where the next read starts.</param>
/// <param name="More">Was a source's page full, so there may be more after <paramref name="NextAfter" />?</param>
internal sealed record IdentityEventChunk(
    IReadOnlyList<IEvent> Sliced,
    IReadOnlyList<IEvent> Stored,
    int CandidateCount,
    long NextAfter,
    bool More);

/// <summary>
/// marten#6003: an aggregation projection that can read one identity's events for the stepper.
/// </summary>
internal interface IIdentityEventReader<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    Task<IdentityEventPage> LoadIdentityEventsAsync(IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, IdentityEventsRequest request, CancellationToken token);
}
