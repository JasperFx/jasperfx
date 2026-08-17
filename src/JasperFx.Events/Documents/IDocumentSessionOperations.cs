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
    /// Commit every pending change enlisted in this session's unit of work as a single transaction.
    /// </summary>
    /// <remarks>
    /// The parameter is named <c>token</c> deliberately and must stay that way: both products spell
    /// it <c>token</c>, and real consumer code passes it by name.
    /// </remarks>
    Task SaveChangesAsync(CancellationToken token = default);
}
