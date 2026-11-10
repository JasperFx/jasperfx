namespace JasperFx.Events.Documents;

/// <summary>
/// Factory for <see cref="IMessageBatch" />, implemented by a message bus integration and installed on a
/// store's options. Called at most once per unit of work, and only when something in it publishes.
/// </summary>
/// <remarks>
/// <para>
/// Takes <see cref="IDocumentSessionOperations" /> so that one implementation can serve every store,
/// which is what Wolverine's per-store <c>*ToWolverineOutbox</c> classes collapse onto. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/1045" />.
/// </para>
/// <para>
/// Lives in this namespace and not beside <see cref="IMessageSink" /> on purpose. Marten, Polecat and
/// Fisher each declare an <c>IMessageOutbox</c> and an <c>IMessageBatch</c>, and none of the files naming
/// those types imports this namespace, so the names collide nowhere. In <c>JasperFx.Events</c> they would
/// be CS0104 across all three stores, and worse in the compliance partials: those are declared inside
/// <c>namespace JasperFx.Events.ComplianceTests</c>, where the enclosing namespace is searched before the
/// file's usings, so <c>: IMessageOutbox</c> would quietly rebind to this type instead of to the store's.
/// </para>
/// <para>
/// A store moves onto these in two steps, so that its integrations can move in their own release. For
/// one release its interfaces derive from these, its batch adds no members, and its outbox forwards the
/// shared member to its own by default interface implementation:
/// </para>
/// <code>
/// public interface IMessageBatch : JasperFx.Events.Documents.IMessageBatch;
///
/// public interface IMessageOutbox : JasperFx.Events.Documents.IMessageOutbox
/// {
///     ValueTask&lt;IMessageBatch&gt; CreateBatch(IDocumentSession session);
///
///     async ValueTask&lt;JasperFx.Events.Documents.IMessageBatch&gt; JasperFx.Events.Documents.IMessageOutbox.CreateBatch(
///         IDocumentSessionOperations session) => await CreateBatch((IDocumentSession)session);
/// }
/// </code>
/// <para>
/// An existing implementer compiles unchanged against that, and the store's options can then be typed
/// to the shared interface. The next release deletes the store's two interfaces. Marten's hooks take
/// different parameters, so it cannot derive; it adapts a shared outbox onto its own interface instead.
/// </para>
/// </remarks>
public interface IMessageOutbox
{
    /// <summary>
    /// Build a batch for the session persisting the write.
    /// </summary>
    /// <remarks>
    /// The session is handed over so that an outbox can enlist in the same unit of work. This is reached
    /// from the daemon's concurrent slice work, so any collection an implementation uses to track the
    /// batches it hands out has to be thread safe too.
    /// </remarks>
    ValueTask<IMessageBatch> CreateBatch(IDocumentSessionOperations session);
}
