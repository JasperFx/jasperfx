using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events.Fetching;

/// <summary>
/// What one stream's <c>FetchForWriting</c> got from the <see cref="IAggregateWriteCache" />: the
/// snapshot to fold onto, if there was a usable one, and where to read the stream's events from.
/// </summary>
/// <remarks>
/// <see langword="default" /> means "no cache involved at all", which is what a
/// <c>FetchManyForWriting</c> plan holds for a stream that does not exist, so a store can keep an array
/// of these without initializing it.
/// </remarks>
public readonly struct AggregateWriteBaseline<T> where T : class
{
    internal AggregateWriteBaseline(IAggregateWriteCache cache, AggregateCacheKey key, T? aggregate, long version)
    {
        Cache = cache;
        Key = key;
        Aggregate = aggregate;
        Version = version;
    }

    internal IAggregateWriteCache? Cache { get; }

    internal AggregateCacheKey Key { get; }

    /// <summary>
    /// The claimed snapshot. Only this caller holds it, so folding onto it in place is safe.
    /// </summary>
    public T? Aggregate { get; }

    /// <summary>
    /// The stream version <see cref="Aggregate" /> is at, or zero when there is no snapshot.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Was a snapshot claimed?
    /// </summary>
    [MemberNotNullWhen(true, nameof(Aggregate))]
    public bool HasAggregate => Aggregate != null;

    /// <summary>
    /// The inclusive version to start reading the stream from: the event after the snapshot, or the
    /// whole stream when there is none.
    /// </summary>
    public long FirstVersionToRead => Version + 1;

    /// <summary>
    /// The stream says there are events past the snapshot and the read returned none, which means they
    /// have been archived out from under it. Folding nothing would hand back a stale aggregate, so the
    /// caller should go on <see cref="WithoutAggregate" /> and read from version 1.
    /// </summary>
    public bool NeedsArchivalRefetch(IReadOnlyList<IEvent> events, long streamVersion)
        => HasAggregate && events.Count == 0 && Version < streamVersion;

    /// <summary>
    /// Give up the snapshot and keep the rest, so a fetch redone from nothing still writes back and the
    /// cache heals in one round rather than two.
    /// </summary>
    public AggregateWriteBaseline<T> WithoutAggregate() => Cache == null ? this : new(Cache, Key, null, 0);
}

/// <summary>
/// One unit of work's dealings with the <see cref="IAggregateWriteCache" />: take a baseline when a
/// stream is fetched for writing, register the aggregate handed to the caller, and write it back after
/// a commit that is known to have happened.
/// </summary>
/// <remarks>
/// <para>
/// <see href="https://github.com/JasperFx/jasperfx/issues/1050" />. Marten
/// (<c>AggregateWriteCacheWriteBack</c>), Polecat (<c>EventOperations</c> and
/// <c>DocumentSessionBase</c>) and Fisher (<c>EventOperations.Writing</c>) each carried their own copy,
/// and each had drifted into a different way of serving a wrong aggregate. The rules below are the
/// union of what they got right. A failure here is not a slow fetch: it is a stale aggregate, a wrong
/// decision, and events that should not exist.
/// </para>
/// <para>
/// <b>Nothing reaches the cache before <see cref="Flush" />.</b> The aggregate belongs to its caller
/// until the commit. Storing it at fetch time, as Marten's Async plan does, puts an instance the caller
/// is still reading where a second session can claim it and fold onto it, which is exactly what
/// take-on-read exists to stop. It also means a failed commit has nothing to compensate for.
/// </para>
/// <para>
/// <b>An entry is labelled with the version the instance reflects, never the version the commit landed
/// on.</b> Register at the version the aggregate was fetched at. Polecat and Fisher apply nothing to the
/// instance they hand out, so that label stays true through the commit and the session's own events are
/// folded on the next fetch. Marten's listener relabelled the fetched instance with the committed
/// version on the theory that the inline projection had mutated it, which is false for an aggregate
/// whose <c>Apply</c> returns a new instance and false when <c>UseIdentityMapForAggregates</c> is off.
/// Either way the entry claimed events it did not contain, and Marten's exact version match then served
/// it. A store whose commit really does produce newer state hands over that instance with
/// <see cref="Advance" />; nothing here relabels what it was given.
/// </para>
/// <para>
/// <b>A store calls <see cref="Flush" /> only when its own commit made the work durable.</b> Anything
/// else is <see cref="Discard()" />: a failed commit, and a session enlisted in a transaction somebody
/// else commits. Dropping is always sound, while a flush the caller then rolls back leaves an entry
/// containing events that never happened, which a later writer can walk the stream back up to.
/// </para>
/// <para>
/// One instance per session, or per tenant scope of one, and like a session it is not thread safe. The
/// cache it talks to is shared and is what takes care of concurrency.
/// </para>
/// </remarks>
public class AggregateWriteCacheSession
{
    // Keyed rather than listed so that everything about one stream of one tenant lands on one slot:
    // AggregateCacheKey is what keeps two tenants' identical stream ids apart, and a second fetch of a
    // stream in one unit of work supersedes the first rather than racing it at flush time.
    private Dictionary<AggregateCacheKey, PendingWrite>? _pending;

    /// <summary>
    /// Is anything waiting to be written back?
    /// </summary>
    public bool HasPendingWrites => _pending is { Count: > 0 };

    /// <summary>
    /// Claim the cached snapshot for a stream whose version this fetch has already read, refusing one
    /// that is ahead of the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot ahead of the stream is what a restore or a rolled-back caller's transaction leaves
    /// behind, and a negative delta cannot be folded. The claim has already removed it, so refusing it
    /// is all the healing required.
    /// </para>
    /// <para>
    /// Call this for a stream that does not exist as well, with zero. That is what removes the entry
    /// of a tombstoned stream. Polecat and Fisher only take when the version is positive, so the entry
    /// outlives the stream and is accepted as a baseline once a new stream with that id gets past it.
    /// </para>
    /// </remarks>
    public AggregateWriteBaseline<T> TakeBaseline<T>(IAggregateWriteCache cache, AggregateCacheKey key,
        long streamVersion) where T : class
    {
        var baseline = TakeBaseline<T>(cache, key);

        return baseline.Version > streamVersion ? baseline.WithoutAggregate() : baseline;
    }

    /// <summary>
    /// Claim the cached snapshot for a stream whose version is not known yet, for a store that has to
    /// decide what goes in the batch that reads the version. Check <see cref="AggregateWriteBaseline{T}.Version" />
    /// against the stream before folding.
    /// </summary>
    public AggregateWriteBaseline<T> TakeBaseline<T>(IAggregateWriteCache cache, AggregateCacheKey key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(cache);

        // A snapshot at version zero or below describes no event at all, and starting from it would fold
        // the whole stream onto whatever instance it is, applying every event twice. Only Fisher screened
        // this. A snapshot of the wrong type means a key collision, which is a miss rather than the
        // InvalidCastException Marten's hard cast would raise from inside a fetch.
        if (cache.TryTake(key, out var claimed, out var version) && claimed is T typed && version > 0)
        {
            return new AggregateWriteBaseline<T>(cache, key, typed, version);
        }

        return new AggregateWriteBaseline<T>(cache, key, null, 0);
    }

    /// <summary>
    /// Owe the cache this aggregate, to be written back by <see cref="Flush" />.
    /// </summary>
    /// <param name="baseline">What the fetch took, which says where the aggregate goes back to.</param>
    /// <param name="aggregate">The instance handed to the caller. A null one registers nothing.</param>
    /// <param name="version">
    /// The stream version this instance reflects: what the fetch read, before anything was appended.
    /// </param>
    public void Register<T>(AggregateWriteBaseline<T> baseline, T? aggregate, long version) where T : class
    {
        // The nullo check is what keeps an unenrolled type allocating nothing, and it is why a store
        // needs no `if (caching enabled)` branch around this call. Version zero is a stream that does
        // not exist yet, which has nothing to cache.
        if (aggregate is null || version <= 0 || baseline.Cache is null or NulloAggregateWriteCache)
        {
            return;
        }

        _pending ??= new Dictionary<AggregateCacheKey, PendingWrite>();
        _pending[baseline.Key] = new PendingWrite(baseline.Cache, aggregate, version);
    }

    /// <summary>
    /// The commit in progress produced newer state for a registered stream: write back this instance at
    /// this version instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a store whose inline projection applies the session's events to the aggregate, which is
    /// Marten. Pass the instance the projection produced, because it is not necessarily the one that
    /// was registered, and the version of the last event it applied. Marten's own write-back matched
    /// the committed stream by id alone, so one session appending to the same stream id under two
    /// tenants could stamp one tenant's aggregate with the other's version. Addressing the write by
    /// <paramref name="key" /> closes that.
    /// </para>
    /// <para>
    /// This changes only what is pending, so it may be called before the commit. A commit that then
    /// fails is the store's <see cref="Discard()" />.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="false" /> when nothing is registered for the key, which is the normal answer for an
    /// aggregate type that did not opt in, or when a later version is already registered.
    /// </returns>
    /// <param name="key">The stream's key, carrying its tenant.</param>
    /// <param name="aggregate">The instance that now holds the stream's state.</param>
    /// <param name="version">The stream version that instance reflects.</param>
    public bool Advance(AggregateCacheKey key, object aggregate, long version)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        if (_pending is null || !_pending.TryGetValue(key, out var existing) || existing.Version > version)
        {
            return false;
        }

        _pending[key] = existing with { Aggregate = aggregate, Version = version };
        return true;
    }

    /// <summary>
    /// Stop owing the cache this one aggregate, because its instance no longer matches the version it
    /// was registered at or the stream's aggregate has been deleted.
    /// </summary>
    /// <remarks>
    /// With an aggregate identity map on, Polecat's <c>ProjectLatest</c> folds pending events onto the
    /// very instance that is registered, before the commit. That instance then contains events its
    /// version says it does not, and the next fetch folds them a second time.
    /// </remarks>
    public void Discard(AggregateCacheKey key)
    {
        _pending?.Remove(key);
    }

    /// <summary>
    /// Write nothing back. For a commit that failed, and for one this session does not own.
    /// </summary>
    /// <remarks>
    /// Has to be called even when the session may simply die. Marten and Fisher let a failed commit's
    /// entries sit until the session was disposed, and a session that survives one, which is what
    /// <c>EjectAllPendingChanges</c> is for, then published them on its next success. For Marten those
    /// are instances an inline projection had already applied the rolled-back events to.
    /// </remarks>
    public void Discard()
    {
        _pending = null;
    }

    /// <summary>
    /// Write everything registered back to its cache. Only after a commit this session owns has
    /// succeeded, and outside any retry of that commit.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IAggregateWriteCache.StoreIfNewer" />, so a slow session finishing late cannot
    /// replace a newer writer's entry with its own older one.
    /// </remarks>
    public void Flush()
    {
        var pending = _pending;
        if (pending is not { Count: > 0 })
        {
            return;
        }

        _pending = null;

        foreach (var (key, write) in pending)
        {
            try
            {
                write.Cache.StoreIfNewer(key, write.Aggregate, write.Version);
            }
            catch (Exception)
            {
                // The commit has already happened. A cache failing here must not turn that into a
                // SaveChangesAsync that reports failure and gets retried, and an entry that was never
                // stored costs one uncached fetch.
            }

            OnPublished(key);
        }
    }

    /// <summary>
    /// <see cref="Flush" /> has finished with this aggregate, whether or not the cache kept it.
    /// </summary>
    /// <remarks>
    /// The seam for a session to give up its own handle on the instance. Polecat evicts it from its
    /// aggregate identity map here, because otherwise the instance is reachable both from the cache,
    /// where another session may claim it, and from a session that may fold onto it. Marten's identity
    /// map has the same exposure.
    /// </remarks>
    protected virtual void OnPublished(AggregateCacheKey key)
    {
    }

    private readonly record struct PendingWrite(IAggregateWriteCache Cache, object Aggregate, long Version);
}
