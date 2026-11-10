using System.Data.Common;
using System.Runtime.ExceptionServices;

namespace JasperFx.Events.Documents;

/// <summary>
/// The per-session state and sequencing behind <see cref="IDocumentSaveListener" /> and
/// <see cref="IDbTransactionParticipant" />. A store holds one per session and calls these three methods
/// at the matching points of its own <see cref="IDocumentSessionOperations.SaveChangesAsync" />.
/// </summary>
/// <remarks>
/// <para>
/// The stores' own versions of this sequence disagree in ways an outbox can see, which is why it is
/// lifted rather than left to three more loops (<see href="https://github.com/JasperFx/jasperfx/issues/1052" />):
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <strong>The message hook is last</strong>, the promise <see cref="IMessageBatch.BeforeCommitAsync" />
/// makes and marten#5353 fought for. Polecat keeps it. Fisher runs participants after it, and Marten's is
/// only last if nothing enlists after the first publish.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>A participant is released when its commit lands.</strong> All three stores keep theirs until
/// the session is disposed, which on a session saved twice writes an envelope row twice.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>After-commit work never sees the caller's token</strong>, Polecat's #783: a client disconnecting
/// as the response is written would otherwise report a committed unit of work as failed. Marten and Fisher
/// pass it through.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>One failing after-commit hook does not starve the next.</strong> All three stop at the first
/// throw, which would let an application's listener keep an outbox from flushing.
/// </description>
/// </item>
/// </list>
/// <para>
/// A store's native listeners and participants stay in its own loops, so a session with no shared hooks
/// behaves exactly as it did. <see cref="AfterCommitAsync" /> goes ahead of the native after-commit loop,
/// in the place Marten and Fisher already call their message batch, so that a native listener that
/// throws cannot starve it.
/// </para>
/// </remarks>
public sealed class DocumentSessionHooks
{
    private readonly List<IDocumentSaveListener> _listeners = new();
    private readonly List<IDbTransactionParticipant> _participants = new();

    // How many of _participants, counting from the front, the latest attempt ran. Enlisting only appends,
    // so this is what a landed commit releases: one enlisted after that attempt started belongs to the
    // next commit, and one no attempt ever reached has not had its turn.
    private int _ran;

    /// <summary>
    /// True while a participant is waiting for a commit.
    /// </summary>
    /// <remarks>
    /// ⚠️ <strong>A store's check for an empty unit of work has to read this.</strong> A participant writes
    /// rows the store does not know about, so "no documents and no events" says nothing about whether there
    /// is work to do. A handler that only cascades a message has exactly this shape, and returning early
    /// there drops the envelope silently: polecat#161 and wolverine#3907. Marten's two early returns in
    /// <c>SaveChangesAsync</c> and <c>ExecuteBatchAsync</c> still do, which is safe only while its envelope
    /// rows travel as queued operations.
    /// </remarks>
    public bool HasPendingParticipants
    {
        get
        {
            lock (_participants)
            {
                return _participants.Count > 0;
            }
        }
    }

    /// <summary>
    /// Run a listener on every save from here on.
    /// </summary>
    public void AddListener(IDocumentSaveListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_listeners)
        {
            _listeners.Add(listener);
        }
    }

    /// <summary>
    /// Enlist a participant in the next commit. A lock because a projection's side effects are raised
    /// concurrently, and enlisting is reachable from there through <see cref="IMessageOutbox" />.
    /// </summary>
    public void AddParticipant(IDbTransactionParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        lock (_participants)
        {
            _participants.Add(participant);
        }
    }

    /// <summary>
    /// Run every listener's <see cref="IDocumentSaveListener.BeforeSaveChangesAsync" />. Call this once at
    /// the start of a save, before the store takes its batch, and only for a unit of work with something
    /// in it. An exception here fails the save with nothing written.
    /// </summary>
    public async Task BeforeSaveAsync(IDocumentSessionOperations session, CancellationToken token)
    {
        foreach (var listener in listeners())
        {
            await listener.BeforeSaveChangesAsync(session, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run every participant, then the message batch. Call this inside the transaction, immediately before
    /// the commit, once per attempt. Participants stay enlisted until
    /// <see cref="AfterCommitAsync" />, so a retried attempt replays them on its fresh transaction.
    /// </summary>
    /// <param name="batch">The session's message batch, or null when nothing published.</param>
    public async Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, IMessageBatch? batch,
        CancellationToken token)
    {
        IDbTransactionParticipant[] participants;
        lock (_participants)
        {
            participants = _participants.ToArray();
            _ran = participants.Length;
        }

        foreach (var participant in participants)
        {
            await participant.BeforeCommitAsync(connection, transaction, token).ConfigureAwait(false);
        }

        if (batch != null)
        {
            await batch.BeforeCommitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Release the participants, then tell the message batch and every listener that the commit landed.
    /// Call this after the commit and outside any retry, and never from a <c>finally</c>: a rolled-back
    /// transaction must not reach it. Its work stays pending for the caller to save again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes no token, so a store cannot pass the caller's: everything here runs after a commit that has
    /// already happened, and <see cref="CancellationToken.None" /> is what reaches the hooks.
    /// </para>
    /// <para>
    /// The batch goes first, matching Marten and Fisher, so that messages from a committed unit of work are
    /// released even if an application's listener then fails. Every hook runs whatever an earlier one did.
    /// The failures are rethrown once the rest have run: the caller of
    /// <see cref="IDocumentSessionOperations.SaveChangesAsync" /> hears about them, though nothing can
    /// un-commit the write.
    /// </para>
    /// </remarks>
    /// <param name="batch">The session's message batch, or null when nothing published.</param>
    public async Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit,
        IMessageBatch? batch)
    {
        lock (_participants)
        {
            _participants.RemoveRange(0, _ran);
            _ran = 0;
        }

        List<Exception>? failures = null;

        if (batch != null)
        {
            try
            {
                await batch.AfterCommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                (failures ??= []).Add(e);
            }
        }

        foreach (var listener in listeners())
        {
            try
            {
                await listener.AfterCommitAsync(session, commit, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                (failures ??= []).Add(e);
            }
        }

        switch (failures)
        {
            case [var only]:
                ExceptionDispatchInfo.Capture(only).Throw();
                break;
            case { Count: > 1 }:
                throw new AggregateException(failures);
        }
    }

    // Copied so that a hook registering another hook does not invalidate the enumeration
    private IDocumentSaveListener[] listeners()
    {
        lock (_listeners)
        {
            return _listeners.ToArray();
        }
    }
}
