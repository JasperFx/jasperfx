namespace JasperFx.Events.Documents;

/// <summary>
/// A committable document session: everything <see cref="IDocumentWriteOperations" /> can enlist,
/// plus the transaction boundary that flushes it.
/// </summary>
/// <remarks>
/// Tier three of three, and the tier a store-agnostic consumer actually holds. Binds to Marten's
/// <c>IDocumentSession</c> and Polecat's <c>IDocumentSession</c>.
/// </remarks>
public interface IDocumentSessionOperations : IDocumentWriteOperations
{
    /// <summary>
    /// The full event store API for this session — read, append, and the aggregate-handler workflow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrows <see cref="IDocumentReadOperations.Events" /> from <see cref="IQueryEventStore" /> to
    /// <see cref="IEventStoreOperations" />, exactly as Marten narrows its own <c>Events</c> from
    /// <c>IQuerySession</c> to <c>IDocumentOperations</c>. Appending belongs to the committable tier
    /// because the append has to ride the session's unit of work: whoever can append is whoever can
    /// <see cref="SaveChangesAsync" />.
    /// </para>
    /// <para>
    /// Note that <see cref="IDocumentWriteOperations" /> — the tier a projection's
    /// <c>RaiseSideEffects</c> receives — deliberately does <em>not</em> carry the narrowing. A
    /// projection may write documents but must not append events or commit; the daemon owns both.
    /// </para>
    /// <para>
    /// Same throwing default and the same non-covariance trap as the read tier; see
    /// <see cref="IDocumentReadOperations.Events" />. Implementing one tier does not implement the
    /// other — a store that satisfies only this one still throws when the session is held as
    /// <see cref="IDocumentReadOperations" />.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation only, when the store has not implemented this member.
    /// </exception>
    new IEventStoreOperations Events
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IDocumentSessionOperations)}.{nameof(Events)}, so events cannot be appended through a session this store opened. Note that C# interface implementation is not return-type covariant: a session declaring an Events property of the product's own event-store type does not satisfy this member, and needs a one-line explicit implementation forwarding to it.");

    /// <summary>
    /// The <see cref="StreamAction" />s this session has queued but not yet committed — every
    /// <c>StartStream</c> and <c>Append</c> enlisted through <see cref="Events" /> since the last
    /// <see cref="SaveChangesAsync" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payload type was already shared; only the spelling of the accessor diverged — Marten's
    /// <c>PendingChanges.Streams()</c>, Polecat's <c>PendingChanges.Streams</c>, and Fisher's
    /// <c>Events.PendingStreams</c>, with no <c>PendingChanges</c> facade at all. So this closes a
    /// naming gap rather than a capability gap, and each store satisfies it by forwarding to what it
    /// already has. See <see href="https://github.com/JasperFx/jasperfx/issues/673" />.
    /// </para>
    /// <para>
    /// Only the streams — deliberately not a whole unit-of-work facade. The three products' change
    /// sets diverge well past this one collection, and the measured consumer need is to read what
    /// the session is about to append, typically from a listener or a pre-commit hook deciding
    /// something from the events already enlisted. Pending <em>document</em> operations are not
    /// exposed here and are not in scope; a consumer needing those takes a dependency on a concrete
    /// store.
    /// </para>
    /// <para>
    /// Committable tier for the same reason <see cref="Events" /> is: a stream action can only be
    /// pending in a session that can append, and appending is
    /// <see cref="IDocumentSessionOperations" />, never
    /// <see cref="IDocumentWriteOperations" />. A projection's <c>RaiseSideEffects</c> holds the
    /// tier below and has no pending streams to read.
    /// </para>
    /// <para>
    /// The list reflects the session at the moment it is read; a store may hand back a snapshot or a
    /// live view, so a caller that needs stability across further appends should copy it. What is
    /// pinned is that it is empty for a session with nothing enlisted, that it carries the actions
    /// enlisted since the last commit, and that committing clears it.
    /// </para>
    /// <para>
    /// ⚠️ Throwing default, and here the choice matters more than it did for <see cref="Events" />:
    /// a default returning an empty list is indistinguishable from a session that genuinely has
    /// nothing pending, so a store that had not implemented the member would silently drop whatever
    /// the consumer derives from these actions — clean build, green tests, no events. The same
    /// non-covariance trap applies as well: a session already declaring its own pending-streams
    /// member of a product-specific shape does not satisfy this one, and needs an explicit
    /// implementation forwarding to it.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation only, when the store has not implemented this member.
    /// </exception>
    IReadOnlyList<StreamAction> PendingStreams
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IDocumentSessionOperations)}.{nameof(PendingStreams)}, so the stream actions queued in this session cannot be read. This member deliberately throws rather than answering with an empty list, because an empty list is indistinguishable from a session with nothing pending and would silently discard whatever the caller derives from the pending events.");

    /// <summary>
    /// Commit every pending change enlisted in this session's unit of work as a single transaction.
    /// </summary>
    /// <remarks>
    /// The parameter is named <c>token</c> deliberately and must stay that way: both products spell
    /// it <c>token</c>, and real consumer code passes it by name.
    /// </remarks>
    Task SaveChangesAsync(CancellationToken token = default);
}
