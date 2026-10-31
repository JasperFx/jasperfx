using System.Linq.Expressions;

namespace JasperFx.Events.Protected;

/// <summary>
///     The shared execution of <see cref="IEventDataMasking" />: resolve every selected event, apply the
///     registered rules, and commit the rewrites. A store supplies its session and its reads (#1048).
/// </summary>
/// <remarks>
///     <para>
///         <b>One session, so one commit.</b> Every source is read and every match rewritten in a single
///         session, and <see cref="ApplyAsync" /> saves it once, last. Anything that fails on the way, a rule
///         above all, leaves that session to be disposed unsaved, so the overwrites already queued go with it
///         and the pass commits nothing. A partial erasure is worse than a failed one: it is a compliance
///         answer that is neither "done" nor "not done", with no record of which events were reached.
///     </para>
///     <para>
///         <b>An event reached twice is masked once.</b> Two sources can overlap, as
///         <c>IncludeStream(id)</c> and an <c>IncludeEvents</c> filter matching that stream do, and masking
///         rules are user code that need not be idempotent. Where the session hands back a fresh copy per
///         read, a second pass over the event runs the rule again and overwrites the first result, so a rule
///         drawing a pseudonym gets recorded with one value and stored with another. Where the reads share an
///         instance, the rule runs over its own output. Fisher deduplicated; Marten and Polecat did not.
///         The key is the tenant and the sequence, not the sequence alone: that is the row a write addresses,
///         because under per-tenant sequences <c>seq_id = 1</c> exists in every tenant, and a read that spans
///         tenants must not see the second tenant's event dropped as a duplicate of the first's.
///     </para>
///     <para>
///         <b>Masking reaches what is stored, not what was derived from it.</b> A projection past an event
///         keeps what it folded, because the daemon's high-water mark does not move, and a rebuild is what
///         clears it. The same goes for a stream compacted before the pass: the folded events are gone and
///         their contents live on in <see cref="Compacted{T}.Snapshot" />, which a rule against the original
///         event types does not match. Register a rule against <see cref="Compacted{T}" /> for that.
///     </para>
/// </remarks>
/// <typeparam name="TSession">The store's document session.</typeparam>
public abstract class EventDataMaskingBase<TSession> : IEventDataMasking where TSession : IAsyncDisposable
{
    private readonly List<Func<TSession, CancellationToken, Task<IReadOnlyList<IEvent>>>> _sources = [];
    private readonly Dictionary<string, object> _headers = new();
    private string? _tenantId;

    /// <inheritdoc />
    public IEventDataMasking ForTenant(string tenantId)
    {
        // Refused rather than read as "no tenant": on a conjoined store that runs the pass against the
        // default tenant, which finds nothing of this tenant's, and reports success.
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        _tenantId = tenantId;
        return this;
    }

    /// <inheritdoc />
    public IEventDataMasking IncludeStream(Guid streamId)
    {
        assertNamesAStream(streamId);

        _sources.Add((session, token) => FetchStreamIncludingArchivedAsync(session, streamId, token));
        return this;
    }

    /// <inheritdoc />
    public IEventDataMasking IncludeStream(string streamKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamKey);

        _sources.Add((session, token) => FetchStreamIncludingArchivedAsync(session, streamKey, token));
        return this;
    }

    /// <inheritdoc />
    public IEventDataMasking IncludeStream(Guid streamId, Func<IEvent, bool> filter)
    {
        assertNamesAStream(streamId);
        ArgumentNullException.ThrowIfNull(filter);

        _sources.Add(async (session, token) =>
        {
            var events = await FetchStreamIncludingArchivedAsync(session, streamId, token).ConfigureAwait(false);
            return events.Where(filter).ToList();
        });

        return this;
    }

    /// <inheritdoc />
    public IEventDataMasking IncludeStream(string streamKey, Func<IEvent, bool> filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamKey);
        ArgumentNullException.ThrowIfNull(filter);

        _sources.Add(async (session, token) =>
        {
            var events = await FetchStreamIncludingArchivedAsync(session, streamKey, token).ConfigureAwait(false);
            return events.Where(filter).ToList();
        });

        return this;
    }

    /// <inheritdoc />
    public IEventDataMasking IncludeEvents(Expression<Func<IEvent, bool>> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _sources.Add((session, token) => QueryEventsIncludingArchivedAsync(session, filter, token));
        return this;
    }

    /// <inheritdoc />
    public IEventDataMasking AddHeader(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);

        _headers[key] = value;
        return this;
    }

    /// <summary>
    ///     Resolve every source, apply the registered rules, and commit the rewrites.
    /// </summary>
    /// <remarks>
    ///     An event only reaches <see cref="IEventStoreOperations.OverwriteEvent" /> when a rule matched it. A
    ///     source that selects events no rule covers writes nothing rather than rewriting them to what they
    ///     already said, and the headers follow, so <see cref="AddHeader" /> marks what was masked rather than
    ///     what was looked at.
    /// </remarks>
    public async Task ApplyAsync(CancellationToken token = default)
    {
        if (_sources.Count == 0)
        {
            throw new InvalidOperationException(
                "Specify at least one stream or event filter before applying a masking batch. "
                + "Masking every event in the store is not something this API can be asked for by accident.");
        }

        await using var session = OpenSession(_tenantId);

        var seen = new HashSet<(string?, long)>();

        foreach (var source in _sources)
        {
            var events = await source(session, token).ConfigureAwait(false);

            foreach (var @event in events)
            {
                // Before the dedupe, which would otherwise read a second unsequenced event as a repeat of the
                // first. The rewrite is addressed by sequence, so this one would match no row and write nothing.
                if (@event.Sequence <= 0)
                {
                    throw new InvalidOperationException(
                        $"An event selected for masking has no sequence, so no stored row can be rewritten. {GetType().FullName} has to read events back from the store.");
                }

                if (!seen.Add((@event.TenantId, @event.Sequence)))
                {
                    continue;
                }

                if (!TryMask(@event))
                {
                    continue;
                }

                foreach (var pair in _headers)
                {
                    @event.Headers ??= new Dictionary<string, object>();
                    @event.Headers[pair.Key] = pair.Value;
                }

                EventsFor(session).OverwriteEvent(@event);
            }
        }

        await SaveChangesAsync(session, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Open the session the whole pass runs on, scoped to <paramref name="tenantId" />, or to the default
    ///     tenant when it is null. The reads and the rewrites both have to go through it: the tenant predicate
    ///     on a rewrite comes from the session, and it has to agree with the tenant the events were read from.
    /// </summary>
    protected abstract TSession OpenSession(string? tenantId);

    /// <summary>
    ///     The session's event operations, where the masked events are overwritten.
    /// </summary>
    protected abstract IEventStoreOperations EventsFor(TSession session);

    /// <summary>
    ///     Every stored event in this stream, in the session's tenant, <b>archived events included</b>.
    /// </summary>
    /// <remarks>
    ///     Archiving a stream does not erase it, so a right-to-erasure pass has to reach it. This is a separate
    ///     member rather than a default over <see cref="IQueryEventStore.FetchStreamAsync(Guid, long, DateTimeOffset?, long, CancellationToken)" />
    ///     because that read leaves archived events out on Marten and Polecat, where the pass would then find
    ///     nothing and report success.
    /// </remarks>
    protected abstract Task<IReadOnlyList<IEvent>> FetchStreamIncludingArchivedAsync(TSession session, Guid streamId,
        CancellationToken token);

    /// <inheritdoc cref="FetchStreamIncludingArchivedAsync(TSession, Guid, CancellationToken)" />
    protected abstract Task<IReadOnlyList<IEvent>> FetchStreamIncludingArchivedAsync(TSession session, string streamKey,
        CancellationToken token);

    /// <summary>
    ///     Every stored event matching this predicate in the session's tenant, <b>archived events included</b>
    ///     unless the predicate itself rules them out.
    /// </summary>
    protected abstract Task<IReadOnlyList<IEvent>> QueryEventsIncludingArchivedAsync(TSession session,
        Expression<Func<IEvent, bool>> filter, CancellationToken token);

    /// <summary>
    ///     Apply every masking rule that matches this event, and report whether any did.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Implement this as a call on the store's own event graph: <c>_store.Events.TryMask(@event)</c>.
    ///     </para>
    ///     <para>
    ///         Abstract, with no default, and with no <see cref="EventRegistry" /> taken by this class, for
    ///         the sake of a store part way through adopting #1043. A store that has not adopted it keeps its
    ///         own rule list behind a hiding <c>TryMask</c>, so a call made through an
    ///         <see cref="EventRegistry" /> reference reads the empty base list and silently masks nothing,
    ///         which on this path is a data protection failure with no error. The call above binds to the
    ///         store's own member while it has one and to
    ///         <see cref="EventRegistry.TryMask" /> once it does not, so the store is right in either order.
    ///     </para>
    /// </remarks>
    protected abstract bool TryMask(IEvent @event);

    /// <summary>
    ///     Commit the session.
    /// </summary>
    protected abstract Task SaveChangesAsync(TSession session, CancellationToken token);

    private static void assertNamesAStream(Guid streamId)
    {
        if (streamId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId), "Cannot use an empty Guid as the stream id");
        }
    }
}
