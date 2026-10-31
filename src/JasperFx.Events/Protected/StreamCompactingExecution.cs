namespace JasperFx.Events.Protected;

/// <summary>
///     The shared execution of <see cref="StreamCompactingRequest{T}" />: fold the stream, offer the events
///     to the archiver, replace the last one with a <see cref="Compacted{T}" /> snapshot and delete the
///     rest. A store supplies the fold and the operations only it can write (#1048).
/// </summary>
/// <remarks>
///     <para>
///         <b>Nothing here commits</b>, and that is what makes a failed snapshot safe. Each step queues onto
///         the caller's session, so the snapshot, the deletes and the watermark land in one transaction or
///         not at all. A store that compacts on its own commit does so when <see cref="ExecuteAsync{T}" />
///         returns true, and any other caller's <c>SaveChangesAsync</c> covers it the same way.
///     </para>
///     <para>
///         <b>The snapshot is queued before the deletes, and the watermark after them</b>, so the queue is safe
///         at every prefix and not only as a whole. Applied in part, it leaves the old events beside the
///         snapshot, which reads the same because <see cref="Compacted{T}.MaybeFastForward" /> folds from the
///         latest snapshot, and a second compaction finishes the job. The other order would leave a window with
///         the events gone and nothing standing in for them.
///     </para>
///     <para>
///         <b>Everything runs through the one session it is given, which is what scopes it to a tenant.</b>
///         Under per-tenant sequences <c>seq_id</c> is not unique across tenants, so the read and every write
///         have to agree on one. A tenant-aware <c>IEventStore.CompactStreamAsync</c> (#910) opens a session
///         for its tenant and passes it here, and nothing in this class takes a tenant of its own.
///     </para>
/// </remarks>
/// <typeparam name="TSession">The store's concrete session.</typeparam>
public abstract class StreamCompactingExecution<TSession>
{
    /// <summary>
    ///     Queue the compaction onto <paramref name="session" />.
    /// </summary>
    /// <returns>
    ///     True when there was something to compact and it was queued. False when the stream has no such
    ///     events, is already compacted to this point, or folds to nothing, in which case nothing was queued.
    /// </returns>
    public async Task<bool> ExecuteAsync<T>(StreamCompactingRequest<T> request, TSession session) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);

        var events = await fetchAsync(request, session).ConfigureAwait(false);

        if (events.Count == 0)
        {
            return false;
        }

        // Already compacted to exactly this point. Writing it again would change nothing but the event's id.
        if (events is [{ Data: Compacted<T> }])
        {
            return false;
        }

        var aggregate = await BuildAggregateAsync<T>(session, events, request.CancellationToken)
            .ConfigureAwait(false);

        // A stream deleted by its own aggregate, or holding nothing that folds to one. Marten and Polecat wrote
        // Compacted<T>(null) here and deleted the events, so the whole history was replaced by an empty snapshot.
        if (aggregate is null)
        {
            return false;
        }

        var last = events[^1];

        // The archiver reads these, so they are set before it runs
        request.Version = last.Version;
        request.Sequence = last.Sequence;

        await archiveAsync(request, session, events).ConfigureAwait(false);

        var compacted = new Compacted<T>(aggregate, request.StreamId ?? Guid.Empty, request.StreamKey ?? string.Empty);

        EventsFor(session).CompletelyReplaceEvent(last.Sequence, compacted);

        if (events.Count > 1)
        {
            DeleteEvents(session, events.Take(events.Count - 1).Select(x => x.Sequence).ToArray());
        }

        RecordCompactionWatermark(session, request.StreamId, request.StreamKey, last.Version);

        return true;
    }

    /// <summary>
    ///     The session's event operations, for the read and for replacing the last event with the snapshot.
    /// </summary>
    protected abstract IEventStoreOperations EventsFor(TSession session);

    /// <summary>
    ///     Fold these events into <typeparamref name="T" />, or null if they fold to nothing.
    /// </summary>
    protected abstract ValueTask<T?> BuildAggregateAsync<T>(TSession session, IReadOnlyList<IEvent> events,
        CancellationToken token) where T : class;

    /// <summary>
    ///     Run <see cref="StreamCompactingRequest{T}.Archiver" /> if it is closed over an operations type this
    ///     store can supply, and report whether it was. Use <see cref="TryArchiveThroughAsync{TOperations,T}" />
    ///     once per closure the store accepts.
    /// </summary>
    /// <remarks>
    ///     <see cref="IEventsArchiver{TOperations}" /> is invariant, so an archiver over
    ///     <c>IDocumentSession</c> is not one over <c>IDocumentOperations</c>, even where the first derives
    ///     from the second. Accept every closure a caller could reasonably write (#423).
    /// </remarks>
    protected abstract Task<bool> TryArchiveAsync<T>(TSession session, StreamCompactingRequest<T> request,
        IReadOnlyList<IEvent> events) where T : class;

    /// <summary>
    ///     Queue the permanent removal of these events, and of anything keyed to them such as their tag rows,
    ///     in the session's tenant. Never called with an empty array.
    /// </summary>
    protected abstract void DeleteEvents(TSession session, long[] sequences);

    /// <summary>
    ///     Queue the compaction watermark for this stream in the session's tenant, so that
    ///     <see cref="StreamState.CompactedVersion" /> reads it back (#740). <paramref name="version" /> is the
    ///     caller's cutoff for a partial compaction and the stream's version for a full one. Exactly one of
    ///     <paramref name="streamId" /> and <paramref name="streamKey" /> is set.
    /// </summary>
    protected abstract void RecordCompactionWatermark(TSession session, Guid? streamId, string? streamKey,
        long version);

    /// <summary>
    ///     Run the request's archiver if it is closed over <typeparamref name="TOperations" />.
    /// </summary>
    protected static async Task<bool> TryArchiveThroughAsync<TOperations, T>(TOperations operations,
        StreamCompactingRequest<T> request, IReadOnlyList<IEvent> events) where T : class
    {
        if (request.Archiver is not IEventsArchiver<TOperations> archiver)
        {
            return false;
        }

        await archiver.MaybeArchiveAsync(operations, request, events, request.CancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    // Branches on what the request names rather than on the store's StreamIdentity. That branch is how the Guid
    // overload on a string store used to read a null key, match nothing and report success, and it is the
    // store's own read that refuses the mismatch. The empty identities are refused because this deletes rows:
    // Marten's read once took them for "no stream predicate", so compaction folded and deleted every event in
    // the tenant, and it must never run wider than a stream the caller named.
    private Task<IReadOnlyList<IEvent>> fetchAsync<T>(StreamCompactingRequest<T> request, TSession session)
        where T : class
    {
        if (request.StreamId is { } streamId)
        {
            if (streamId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Cannot use an empty Guid as the stream id");
            }

            return EventsFor(session).FetchStreamAsync(streamId, request.Version, request.Timestamp,
                token: request.CancellationToken);
        }

        if (string.IsNullOrEmpty(request.StreamKey))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The stream key cannot be null or empty");
        }

        return EventsFor(session).FetchStreamAsync(request.StreamKey, request.Version, request.Timestamp,
            token: request.CancellationToken);
    }

    private async Task archiveAsync<T>(StreamCompactingRequest<T> request, TSession session,
        IReadOnlyList<IEvent> events) where T : class
    {
        if (request.Archiver is null || await TryArchiveAsync(session, request, events).ConfigureAwait(false))
        {
            return;
        }

        // Each store matched the closures it knew and skipped the rest, then deleted the events anyway, which
        // is the opposite of what the hook is for. Past this point the events are gone.
        throw new InvalidOperationException(
            $"{request.Archiver.GetType().FullName} is not closed over an operations type {GetType().FullName} can supply, so it cannot archive these events. Nothing was compacted.");
    }
}
