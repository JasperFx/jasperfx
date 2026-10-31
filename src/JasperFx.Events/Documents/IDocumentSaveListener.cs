namespace JasperFx.Events.Documents;

/// <summary>
/// An <see cref="IDocumentCommitListener" /> that also runs at the start of
/// <see cref="IDocumentSessionOperations.SaveChangesAsync" />, before anything is written. Registered with
/// <see cref="IDocumentSessionOperations.AddSaveListener" /> on the one session that needs it.
/// </summary>
/// <remarks>
/// <para>
/// All three products declare this pair as <c>BeforeSaveChangesAsync</c> and <c>AfterCommitAsync</c> on
/// their own <c>IDocumentSessionListener</c>, so a message bus outbox had to be written once per store
/// against three listener types. See <see href="https://github.com/JasperFx/jasperfx/issues/1052" />.
/// </para>
/// <para>
/// A separate interface rather than a member on <see cref="IDocumentCommitListener" /> on purpose. Marten's
/// <c>DocumentCommitListenerAdapter</c>, Polecat's <c>CommitListeners</c> and Fisher's
/// <c>AsSessionListener()</c> all forward the after-commit half and nothing else, so a before-save member
/// added there would be silently skipped by the wiring that already ships, and an outbox listening on it
/// would never enlist. This type only reaches a store through a registration the store has to implement.
/// </para>
/// <para>
/// Named for what it adds, because <c>IDocumentSessionListener</c> belongs to all three stores: a file
/// importing a store's namespace and this one would be CS0104 on every mention.
/// </para>
/// <para>
/// Neither hook fires for a unit of work with nothing in it, exactly as
/// <see cref="IDocumentCommitListener.AfterCommitAsync" /> does not, so a listener cannot be used as a
/// commit counter. A queued <see cref="IDbTransactionParticipant" /> is not nothing, though: see
/// <see cref="DocumentSessionHooks.HasPendingParticipants" />.
/// </para>
/// </remarks>
public interface IDocumentSaveListener : IDocumentCommitListener
{
    /// <summary>
    /// Called at the start of <see cref="IDocumentSessionOperations.SaveChangesAsync" />, outside the
    /// transaction and before anything is written. Called once per save, not once per retried attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A document or an <see cref="IDbTransactionParticipant" /> enlisted here joins the same commit, which
    /// is what an outbox does in this hook. An exception fails the unit of work with nothing written.
    /// </para>
    /// <para>
    /// ⚠️ <strong>Do not append events from here.</strong> Marten has already processed the session's
    /// streams by this point, while Polecat and Fisher have not, so an append commits on two stores and is
    /// lost on the third. Reading <see cref="IDocumentSessionOperations.PendingStreams" /> is safe
    /// everywhere, but for the same reason nothing here should depend on an event's <c>Sequence</c> or
    /// <c>Version</c>.
    /// </para>
    /// </remarks>
    Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token);
}
