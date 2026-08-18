namespace JasperFx.Events.Documents;

/// <summary>
/// A callback invoked after a document session commits successfully, carrying the documents that
/// commit wrote.
/// </summary>
/// <remarks>
/// <para>
/// The store-agnostic counterpart to Marten's <c>DocumentSessionListenerBase</c> and to Polecat's
/// and Fisher's <c>IDocumentSessionListener</c>. All three already declare
/// <c>Task AfterCommitAsync(IDocumentSession, IChangeSet, CancellationToken token)</c> — the same
/// method, differing only in the two store-local types it names. So, as with
/// <see cref="IDocumentSessionOperations.PendingStreams" />, this closes a naming gap rather than a
/// capability gap. Marten's being a convenience base class while the other two are interfaces is an
/// implementation detail of Marten's base, not a real difference. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/679" />.
/// </para>
/// <para>
/// <strong>This is the SESSION half only.</strong> It fires for commits made through a session the
/// application opened. It does <em>not</em> fire for the async daemon's projection batches — JasperFx
/// already owns that half as <see cref="Daemon.IDaemonChangeListener" />, and no store routes
/// projection writes through its session listeners. Stated here because the failure mode is a
/// consumer registering one listener, seeing application writes arrive, and concluding the store is
/// dropping projection writes. A consumer that wants both registers both.
/// </para>
/// <para>
/// Deliberately post-commit only. A <c>BeforeCommit</c> counterpart is not declared: no measured
/// consumer needs one, and a pre-commit hook wanting to read what is about to be appended is already
/// served by <see cref="IDocumentSessionOperations.PendingStreams" />. It can be added additively if
/// a case turns up.
/// </para>
/// <para>
/// <strong>Registration is the store's concern.</strong> Each product already owns a
/// <c>Listeners</c> collection on its own <c>StoreOptions</c> and <c>SessionOptions</c>, and a store
/// adopting this contract adapts an <see cref="IDocumentCommitListener" /> onto its own listener
/// type from its own <c>AddMarten</c> / <c>AddPolecat</c> / <c>AddFisher</c> callback, the same way
/// the jasperfx#647 contracts are registered. Nothing here names a container or an options type.
/// </para>
/// <para>
/// ⚠️ <strong>Where the silent failure lives, and it is not where it was for
/// <see cref="IDocumentSessionOperations.Events" />.</strong> No member of this contract has a
/// default implementation, so the non-covariance trap of jasperfx#669 cannot bite here: a store type
/// declaring <c>: IDocumentChangeSet</c> whose existing <c>Inserted</c> is an
/// <c>IEnumerable&lt;object&gt;</c> rather than an <see cref="IReadOnlyList{T}" /> gets CS0535 at
/// build time, not a member that silently binds to a throwing default. What the compiler cannot see
/// is the <em>wiring</em>: a store that declares both interfaces perfectly and then never invokes
/// the listener produces no error anywhere, and the consumer simply never hears about a commit. That
/// is the failure the compliance suite exists to catch, and the reason a green build is not evidence
/// this contract is satisfied.
/// </para>
/// </remarks>
public interface IDocumentCommitListener
{
    /// <summary>
    /// Called after <see cref="IDocumentSessionOperations.SaveChangesAsync" /> has committed
    /// successfully.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>After a successful commit only.</strong> A commit that throws must not raise this —
    /// the contract exists so a consumer can act on writes that landed, and a listener firing on a
    /// rolled-back transaction would announce work the database never took. That is the fact a store
    /// is most likely to get wrong by wiring the call into a <c>finally</c>, so it is pinned by the
    /// compliance suite rather than left to the store's discretion.
    /// </para>
    /// <para>
    /// ⚠️ <strong>Two cases where a store may legitimately NOT fire, and they are not uniform across
    /// the products.</strong> Both are stated here so that neither a consumer nor a compliance suite
    /// assumes a uniformity that does not exist:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <strong>An empty unit of work.</strong> A <c>SaveChangesAsync</c> with nothing enlisted need
    /// not raise a commit that wrote nothing. Fisher short-circuits; Marten's behavior is the same
    /// but was never stated. Depend on the callback for writes, never as a commit counter.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <strong>A session enlisted in a caller's ambient transaction.</strong> Fisher deliberately
    /// does not fire for one, because the session's <c>SaveChangesAsync</c> is not the point at
    /// which the data becomes durable — the enclosing transaction is, and the store cannot see it
    /// commit. Marten fires unconditionally. This contract does not force either behavior, because
    /// forcing Marten's would make the callback announce writes an outer rollback can still discard.
    /// A consumer relying on the callback under an ambient transaction must check its store.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// One further divergence, noted so it is not mistaken for a dropped write: Marten ejects
    /// patched document types from the change set <em>before</em> running its listener loop, so
    /// documents modified by a patch operation are already absent from
    /// <paramref name="commit" /> when a listener sees it.
    /// </para>
    /// </remarks>
    /// <param name="session">
    /// The session that committed. It is still usable — a listener may query through it, and may
    /// enlist further work, though anything it enlists belongs to the <em>next</em> commit and is
    /// not part of the transaction that just landed.
    /// </param>
    /// <param name="commit">What that commit wrote.</param>
    /// <param name="token">
    /// Named <c>token</c> deliberately and must stay that way: all three products spell it
    /// <c>token</c>, as does <see cref="IDocumentSessionOperations.SaveChangesAsync" />.
    /// </param>
    /// <returns>
    /// A <see cref="Task" /> rather than a <see cref="ValueTask" />, matching all three products'
    /// existing <c>AfterCommitAsync</c>. A per-commit hook is not a hot path, and a
    /// <see cref="ValueTask" /> here would buy nothing while turning every store's forward into an
    /// allocation-wrapping adapter.
    /// </returns>
    Task AfterCommitAsync(
        IDocumentSessionOperations session,
        IDocumentChangeSet commit,
        CancellationToken token);
}

/// <summary>
/// The documents written by a single committed transaction.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately three collections and nothing else, on the jasperfx#647 precedent of shipping the
/// measured surface rather than the imagined one. All three products' change sets also carry
/// <c>GetEvents()</c> and <c>GetStreams()</c>; those are not guessed at here and can be added
/// additively when a consumer needs them. A consumer wanting the event side of a commit today reads
/// <see cref="IDocumentSessionOperations.PendingStreams" /> from a pre-commit position instead.
/// </para>
/// <para>
/// <strong>The collections are SNAPSHOTS, and that is load-bearing rather than a nicety.</strong>
/// They are <see cref="IReadOnlyList{T}" /> and not <see cref="IEnumerable{T}" /> because on at
/// least one product the change set is not a value at all — Marten's <c>IChangeSet</c> <em>is</em>
/// the session's live unit of work, and it is reset immediately after the listener loop runs. A lazy
/// sequence handed out from it is empty or wrong by the time a listener that stashed it enumerates
/// again. Requiring a materialized list forces each store to copy when it builds the change set,
/// which is also why nothing here mirrors Marten's <c>IChangeSet.Clone()</c>: with the copy made at
/// construction there is no live object left to defend against, so the shared contract needs no
/// clone step and a consumer needs no rule about when to call one.
/// </para>
/// <para>
/// Empty rather than null throughout: a commit that inserted nothing has an empty
/// <see cref="Inserted" />. There is no null case for a consumer to guard.
/// </para>
/// </remarks>
public interface IDocumentChangeSet
{
    /// <summary>
    /// Documents this commit created.
    /// </summary>
    /// <remarks>
    /// Whether a given <c>Store</c> counts as an insert or an update is the store's own
    /// determination and is not held to a shared definition here — products differ on how much they
    /// know before the write. What is pinned is that every document the commit wrote appears in
    /// exactly one of <see cref="Inserted" /> or <see cref="Updated" />.
    /// </remarks>
    IReadOnlyList<object> Inserted { get; }

    /// <summary>
    /// Documents this commit overwrote.
    /// </summary>
    IReadOnlyList<object> Updated { get; }

    /// <summary>
    /// The deletions this commit performed, as <see cref="IDocumentDeletion" /> descriptors rather
    /// than as document instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <strong>Descriptors, and it cannot be otherwise.</strong> Of the three products only
    /// Marten's change set holds a deleted document instance; Polecat's and Fisher's carry
    /// <c>{ DocumentType, Id }</c> and nothing more. A <c>Deleted</c> declared as
    /// <c>IReadOnlyList&lt;object&gt;</c> — symmetrical with <see cref="Inserted" /> and
    /// <see cref="Updated" />, and the obvious shape to reach for — would therefore be
    /// unimplementable on two of the three stores, which could only ever answer it empty.
    /// </para>
    /// <para>
    /// It is also the honest shape rather than merely the achievable one. Deletion by identity
    /// (<see cref="IDocumentWriteOperations.Delete{T}(System.Guid)" />) and by criteria
    /// (<see cref="IDocumentWriteOperations.DeleteWhere{T}" />) never loaded a document to report,
    /// so a collection of instances would silently omit exactly the deletions a consumer is least
    /// likely to expect to be missing.
    /// </para>
    /// </remarks>
    IReadOnlyList<IDocumentDeletion> Deleted { get; }
}

/// <summary>
/// One document removed by a committed transaction, identified by type and identity.
/// </summary>
/// <remarks>
/// <para>
/// The two members every product's change set already carries, so no store has to project anything
/// new. Polecat's <c>Polecat.Services.IDeletion</c> and Fisher's
/// <c>Fisher.Services.IDocumentDeletion</c> declare exactly this pair; Marten's deleted element is a
/// live <c>Weasel.Storage.IDeletion</c> storage operation, which inherits <c>Type DocumentType</c>
/// from <c>Weasel.Core.IStorageOperation</c> and declares <c>object Id</c>.
/// </para>
/// <para>
/// Owned by JasperFx on purpose. Marten's deleted element type lives in <c>Weasel</c>, so exposing
/// it directly would re-couple every consumer of this contract to a package it otherwise never
/// names — the same coupling the document contracts exist to remove.
/// </para>
/// <para>
/// Named for Fisher's spelling. <c>IDeletion</c> is already taken in any file that also imports
/// <c>Weasel.Storage</c>, which is the situation inside Marten and the reason Fisher renamed it; a
/// shared contract reintroducing that collision would have to be aliased in the one store that most
/// needs to implement it cleanly.
/// </para>
/// <para>
/// No document instance is exposed, even though Marten has one. Two of the three products cannot
/// supply it, and a member populated on one store and empty on the others is worse than an absent
/// one — it reads as a store bug rather than as a contract boundary. A consumer needing the deleted
/// document reads it before the delete, or takes a dependency on a concrete store.
/// </para>
/// </remarks>
public interface IDocumentDeletion
{
    /// <summary>
    /// The document type that was deleted.
    /// </summary>
    Type DocumentType { get; }

    /// <summary>
    /// The identity of the deleted document, or <c>null</c> where the deletion was expressed as
    /// criteria (<see cref="IDocumentWriteOperations.DeleteWhere{T}" />) and so names no single
    /// identity.
    /// </summary>
    object? Id { get; }
}
