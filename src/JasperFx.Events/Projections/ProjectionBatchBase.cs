using System.Collections.Concurrent;
using JasperFx.Events.Daemon;
using JasperFx.Events.Documents;

namespace JasperFx.Events.Projections;

/// <summary>
/// The store-agnostic half of an async daemon projection batch: the sessions it collected, the events
/// its projections raised, the messages they published, and the order the commit hooks run in.
/// </summary>
/// <remarks>
/// <para>
/// Lifted from <c>PolecatProjectionBatch</c> and <c>FisherProjectionBatch</c>, which carried this code
/// line for line around a store-specific transaction. Marten's <c>ProjectionUpdateBatch</c> queues
/// operations onto a work tracker rather than draining sessions, so it stays as it is. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/1045" />.
/// </para>
/// <para>
/// A store closes <see cref="IProjectionBatch{TOperations,TQuerySession}" /> on its own batch type, which
/// the inherited <see cref="SessionForTenant" /> satisfies:
/// </para>
/// <code>
/// internal class PolecatProjectionBatch : ProjectionBatchBase&lt;IDocumentSession&gt;,
///     IProjectionBatch&lt;IDocumentSession, IQuerySession&gt;
/// </code>
/// <para>
/// ⚠️ <strong>Side effects run in <see cref="ShardExecutionMode.Continuous" /> only</strong>, and that
/// includes reaching the outbox at all. A rebuild replays history, so a message published from it is a
/// second copy of a message that already went out, years late, with nothing downstream able to tell.
/// Losing that message costs nothing, because the original delivery happened, and sending it can mail
/// every customer again. <see cref="AggregationRunner{TDoc,TId,TOperations,TQuerySession}" /> already
/// holds side effects back, so this is the second gate rather than the only one, and it exists because
/// the first one has failed before: marten#4729 ran composite members in Continuous during a composite
/// rebuild, and their side effects fired.
/// </para>
/// <para>
/// <see cref="ShardExecutionMode.CatchUp" /> is gated the same way, as it is by the runner, because it is
/// what an optimized rebuild runs in.
/// </para>
/// </remarks>
/// <typeparam name="TSession">
/// The store's committable session. Constrained to <see cref="IDocumentSessionOperations" /> because that
/// is what <see cref="IMessageOutbox.CreateBatch" /> takes.
/// </typeparam>
public abstract class ProjectionBatchBase<TSession> : IProjectionBatch
    where TSession : class, IDocumentSessionOperations, IStorageOperations
{
    // A queue rather than the stores' ConcurrentBag, so that "the first session" means the same thing on
    // every run: a bag enumerates per thread, and the outbox is handed whichever one comes up first.
    private readonly ConcurrentQueue<TSession> _sessions = new();

    private readonly List<StreamAction> _raisedEvents = new();

    // Null until a projection in this batch actually publishes, which keeps both commit hooks no-ops for
    // the common case of no bus integration.
    private IMessageBatch? _messageBatch;
    private readonly SemaphoreSlim _messageBatchGate = new(1, 1);

    private bool _disposed;

    /// <param name="mode">
    /// Required rather than defaulted: a batch that does not know it is in a rebuild publishes as though
    /// it were not.
    /// </param>
    protected ProjectionBatchBase(ShardExecutionMode mode)
    {
        Mode = mode;
    }

    /// <summary>
    /// The mode of the shard this batch is working for.
    /// </summary>
    public ShardExecutionMode Mode { get; }

    /// <summary>
    /// False when this batch is replaying history, and therefore must not re-fire anything that leaves
    /// the database. A store's own commit listeners should honor this as well.
    /// </summary>
    protected bool SideEffectsEnabled => Mode == ShardExecutionMode.Continuous;

    /// <summary>
    /// The outbox configured on the store.
    /// </summary>
    protected abstract IMessageOutbox Outbox { get; }

    /// <summary>
    /// Every session this batch has collected, in the order they joined it. Safe to enumerate while
    /// another thread adds one.
    /// </summary>
    protected IReadOnlyCollection<TSession> Sessions => _sessions;

    /// <summary>
    /// Every distinct stream a projection in this batch raised events onto.
    /// </summary>
    /// <remarks>
    /// A copy, never consumed, so a store reading it inside a retried transaction sees the same list on
    /// every attempt (fisher#12).
    /// </remarks>
    protected IReadOnlyList<StreamAction> RaisedEvents
    {
        get
        {
            lock (_raisedEvents)
            {
                return _raisedEvents.ToArray();
            }
        }
    }

    /// <summary>
    /// Open a session for one tenant's slice of this batch. The session must be pinned to the database
    /// this batch is working, rather than resolved from the tenant id.
    /// </summary>
    protected abstract TSession OpenSessionForTenant(string tenantId);

    /// <summary>
    /// Write everything this batch holds, in one transaction, then commit it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what a store has to supply: pulling the work out of <see cref="Sessions" />,
    /// the connection, the transaction, the resilience around them, the raised events and progression,
    /// and the transaction's own participants. A retry runs inside this method, so everything it consumes
    /// has to be taken before the first attempt.
    /// </para>
    /// <para>
    /// ⚠️ <paramref name="beforeCommit" /> has to be awaited on every attempt, as the last thing before
    /// the commit, and after the store's own participants: the batch is being told that the transaction
    /// is about to commit, and nothing may fail after it hears that (marten#5353). A store skipping the
    /// transaction because it has nothing to write still has to call it, because a projection can
    /// publish without writing a document.
    /// </para>
    /// </remarks>
    protected abstract Task ExecuteOperationsInTransactionAsync(Func<CancellationToken, Task> beforeCommit,
        CancellationToken token);

    /// <summary>
    /// Plan the raised events ahead of the transaction, for a store whose append needs its own read of
    /// the stream state. Not called when no events were raised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs before <see cref="ExecuteOperationsInTransactionAsync" />, so a tenant session opened here is
    /// collected with the rest. <see cref="IProjectionBatch.QuickAppendEvents" /> and its siblings are
    /// synchronous, and numbering a raised event correctly takes a read, which is why they only record.
    /// </para>
    /// <para>
    /// A store whose read has to happen under the transaction's own write lock leaves this alone and
    /// reads <see cref="RaisedEvents" /> from inside <see cref="ExecuteOperationsInTransactionAsync" />.
    /// </para>
    /// </remarks>
    protected virtual Task PlanRaisedEventsAsync(IReadOnlyList<StreamAction> raisedEvents, CancellationToken token)
        => Task.CompletedTask;

    /// <summary>
    /// The store's own post-commit work, such as commit listeners and participants that were told the
    /// write is durable. Runs after the message batch's after-commit hook.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes no token, deliberately. See the comment in <see cref="ExecuteAsync" />.
    /// </para>
    /// <para>
    /// An exception escaping from here fails a batch that has already committed, so a hook that is only
    /// best effort has to swallow its own.
    /// </para>
    /// </remarks>
    protected virtual Task AfterCommitAsync() => Task.CompletedTask;

    /// <summary>
    /// A session for one tenant's slice of this batch.
    /// </summary>
    /// <remarks>
    /// Thread safe, because a composite projection asks for sessions concurrently.
    /// </remarks>
    public TSession SessionForTenant(string tenantId)
    {
        var session = OpenSessionForTenant(tenantId);
        _sessions.Enqueue(session);
        return session;
    }

    /// <summary>
    /// Include a session created elsewhere, such as a subscription's, in this batch.
    /// </summary>
    public void RegisterSession(TSession session) => _sessions.Enqueue(session);

    /// <inheritdoc />
    public abstract ValueTask RecordProgress(EventRange range);

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken token)
    {
        var raisedEvents = RaisedEvents;
        if (raisedEvents.Count > 0)
        {
            await PlanRaisedEventsAsync(raisedEvents, token).ConfigureAwait(false);
        }

        var messageBatch = _messageBatch;

        await ExecuteOperationsInTransactionAsync(
            messageBatch is null ? static _ => Task.CompletedTask : messageBatch.BeforeCommitAsync,
            token).ConfigureAwait(false);

        // ⚠️ Outside the store's resilience pipeline and NOT on `token`, and neither is an accident.
        //
        // A retried transaction re-runs everything inside that pipeline, so a post-commit hook in there
        // would fire again for a transaction that committed once.
        //
        // And a drain that times out cancels exactly the token this method received (jasperfx#953), which
        // can land between the commit returning and these hooks running. The page is durably committed and
        // its progression has advanced, so it will never be reprocessed: anything abandoned here is lost
        // outright rather than deferred (polecat#744, fisher#434). A slow flush can outlast the drain
        // deadline as a result, and that is the right trade, because the daemon's hard stop still bounds
        // shutdown and a late flush can be recovered from where a lost one cannot.
        //
        // The message batch failing does not excuse the store's own hooks, because the write landed
        // either way, and a participant waiting to hear that should still hear it.
        try
        {
            if (messageBatch is not null)
            {
                await messageBatch.AfterCommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await AfterCommitAsync().ConfigureAwait(false);
        }
    }

    // polecat#420, fisher#61: EventSlice.BuildOperations drives these three whenever a projection raises
    // an event, and an empty implementation loses the events with no error at all. All three funnel into
    // one list because the StreamAction already carries every raised event, and the single-stream-start
    // path passes the SAME instance to each call (once per event to QuickAppendEventWithVersion, then
    // once to UpdateStreamVersion), so reference identity dedupes them into one append per stream.

    /// <inheritdoc />
    public void QuickAppendEventWithVersion(StreamAction action, IEvent @event) => recordRaisedEvents(action);

    /// <inheritdoc />
    public void UpdateStreamVersion(StreamAction action) => recordRaisedEvents(action);

    /// <inheritdoc />
    public void QuickAppendEvents(StreamAction action) => recordRaisedEvents(action);

    private void recordRaisedEvents(StreamAction action)
    {
        // The same reasoning as publishing, with a worse failure: an event raised again by a rebuild lands
        // on the stream for good, takes a new sequence and grows the stream on every rebuild.
        if (!SideEffectsEnabled) return;

        lock (_raisedEvents)
        {
            // Reference identity, not stream id: two distinct actions for the same stream in one batch
            // are two real appends, while the same instance arriving three times is one. A scan rather
            // than a set because StreamAction overrides neither Equals nor GetHashCode, so a set would
            // compare by identity while reading as though it compared by value.
            foreach (var existing in _raisedEvents)
            {
                if (ReferenceEquals(existing, action)) return;
            }

            _raisedEvents.Add(action);
        }
    }

    /// <inheritdoc />
    public async Task PublishMessageAsync(object message, string tenantId)
    {
        if (!SideEffectsEnabled) return;

        var batch = await currentMessageBatchAsync().ConfigureAwait(false);
        await MessagePublishing.PublishAsync(batch, message, tenantId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishMessageAsync(object message, MessageMetadata metadata)
    {
        if (!SideEffectsEnabled) return;

        var batch = await currentMessageBatchAsync().ConfigureAwait(false);
        await MessagePublishing.PublishAsync(batch, message, metadata).ConfigureAwait(false);
    }

    private async ValueTask<IMessageBatch> currentMessageBatchAsync()
    {
        if (_messageBatch is not null) return _messageBatch;

        // Guarded because a composite projection publishes from several slices concurrently, and each
        // batch has to get exactly one IMessageBatch. The second check is inside the try so that
        // returning from it still releases the gate: marten#4727 leaked the semaphore that way, and every
        // parallel slice in a composite rebuild parked on WaitAsync forever.
        await _messageBatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_messageBatch is not null) return _messageBatch;

            // Opened rather than refused when the batch has no session yet, which is Fisher's answer.
            // Polecat threw here, but a projection publishing without writing a document is ordinary.
            var session = _sessions.TryPeek(out var first)
                ? first
                : SessionForTenant(StorageConstants.DefaultTenantId);

            return _messageBatch = await Outbox.CreateBatch(session).ConfigureAwait(false);
        }
        finally
        {
            _messageBatchGate.Release();
        }
    }

    /// <summary>
    /// Dispose every session this batch collected. A store with anything that has to go first, like a
    /// participant still holding a session's connection, overrides this and calls the base last.
    /// </summary>
    /// <remarks>
    /// Idempotent, as Marten's batch is, so that no teardown path has to know whether another got there
    /// first.
    /// </remarks>
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _messageBatchGate.Dispose();

        foreach (var session in _sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
