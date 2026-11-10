namespace JasperFx.Events.Documents;

/// <summary>
/// A message-publishing surface scoped to one unit of work: one session's
/// <see cref="IDocumentSessionOperations.SaveChangesAsync" />, or one projection daemon batch.
/// </summary>
/// <remarks>
/// <para>
/// A projection publishes through <see cref="IMessageSink" />, and the batch buffers what it is given
/// and flushes it in whichever of the two hooks its delivery guarantee calls for.
/// <see cref="BeforeCommitAsync" /> is where an outbox persists rows that have to be atomic with the
/// write; <see cref="AfterCommitAsync" /> is where a best-effort publish belongs, since it must never
/// fire for a transaction that rolled back. See <see href="https://github.com/JasperFx/jasperfx/issues/1045" />.
/// </para>
/// <para>
/// Lifted from Polecat's and Fisher's identically shaped interfaces, whose hooks take a bare
/// <see cref="CancellationToken" />. Marten's hooks also receive the session and the change set, because
/// its interface derives from <c>IChangeListener</c>, but the only production implementers ignore both:
/// all three of Wolverine's bridges have a no-op before hook and an after hook that flushes
/// <c>ProjectionSideEffectSink</c>. The narrow shape is the one all three can implement.
/// </para>
/// <para>
/// ⚠️ <strong>Implementations must be thread safe.</strong> The daemon raises side effects for the
/// slices in one batch concurrently, so <see cref="IMessageSink.PublishAsync{T}(T, string)" /> is called
/// from several threads against one instance. An implementation appending to an unsynchronized
/// collection silently loses messages under load, which presented as a flaky test in marten#5065.
/// </para>
/// </remarks>
public interface IMessageBatch : IMessageSink
{
    /// <summary>
    /// Called inside the write transaction, as the last thing before the commit, after every operation
    /// in the unit of work has succeeded.
    /// </summary>
    /// <remarks>
    /// Called once per attempt rather than once per batch. A store that retries a failed transaction
    /// calls this again, because whatever the first call wrote was rolled back with it.
    /// </remarks>
    Task BeforeCommitAsync(CancellationToken token);

    /// <summary>
    /// Called once, after the write is durably committed. Never called for a transaction that failed.
    /// </summary>
    /// <remarks>
    /// A projection daemon batch passes <see cref="CancellationToken.None" />, because the page is already
    /// committed and its progression has advanced: work abandoned here is lost, not deferred.
    /// </remarks>
    Task AfterCommitAsync(CancellationToken token);
}
