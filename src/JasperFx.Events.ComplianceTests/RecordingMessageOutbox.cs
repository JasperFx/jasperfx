using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The message outbox a store publishes projection side effects through, recording what it was
/// asked to do, for <see cref="ProjectionSideEffectCompliance{TFixture,TOperations,TQuerySession}" />.
/// </summary>
/// <remarks>
/// <para>
/// Same shape and same purpose as <see cref="RecordingAggregateWriteCache" />: the suite supplies
/// the collaborator so it can assert a <em>nonzero</em> publish count. Every behavioral fact about
/// side effects is vacuously true of a store that dropped them on the floor — which is not a
/// hypothetical, since two of the three products shipped the raise seam stubbed empty and lost
/// every raised event with no error and no log (fisher#61, polecat#420).
/// </para>
/// <para>
/// This is the third shared type in the library that cannot be reached by an alias alone, after
/// <c>ComplianceFlatTableProjection</c> and <see cref="ComplianceSubscription" />, and for the same
/// reason as the second: each product still declares its own <c>IMessageOutbox</c> and
/// <c>IMessageBatch</c>, and their members differ. Marten declares
/// <c>IMessageBatch : IMessageSink, IChangeListener</c>, so its commit hooks take
/// <c>(IDocumentSession, IChangeSet, CancellationToken)</c>, while Polecat and Fisher declare their own
/// two-member interface taking a bare <c>CancellationToken</c>. The library owns the recording, the
/// ordering and the probe; a consumer supplies only the interface implementation:
/// </para>
/// <code>
/// public partial class RecordingMessageOutbox : IMessageOutbox
/// {
///     public ValueTask&lt;IMessageBatch&gt; CreateBatch(DocumentSessionBase session)
///         =&gt; new(NewBatch());
/// }
///
/// public partial class RecordingMessageBatch : IMessageBatch
/// {
///     public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
///         =&gt; RecordBeforeCommitAsync();
///
///     public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
///         =&gt; RecordAfterCommitAsync();
/// }
/// </code>
/// <para>
/// The library half already implements the shared <see cref="JasperFx.Events.Documents.IMessageOutbox" />
/// and <see cref="JasperFx.Events.Documents.IMessageBatch" /> (jasperfx#1045), so a store that has moved
/// onto those deletes its half of this type altogether. The implementations are <em>explicit</em>, and
/// that is load-bearing. This package ships as source, and an existing consumer's half declares a public
/// <c>BeforeCommitAsync(CancellationToken)</c> of its own, so a public member here would be CS0111 in
/// that consumer the day it took this version. Written explicitly, the two coexist in every state a
/// store can be in: on its own interfaces, on interfaces deriving from the shared ones, or on the shared
/// ones alone. The names are fully qualified for the same reason, since the consumer owns the usings
/// that decide what a bare <c>IMessageOutbox</c> means.
/// </para>
/// <para>
/// Recording is under a lock because the daemon raises side effects for the slices in one batch
/// concurrently. Marten's own <c>RecordingMessageOutbox</c> carries a comment saying exactly that,
/// added after an unguarded <c>List.Add</c> silently dropped batches and presented as flakiness
/// (marten#5065) — so the shared copy is synchronized from the start.
/// </para>
/// </remarks>
public partial class RecordingMessageOutbox : JasperFx.Events.Documents.IMessageOutbox
{
    private readonly object _locker = new();
    private readonly List<RecordingMessageBatch> _batches = new();

    ValueTask<JasperFx.Events.Documents.IMessageBatch> JasperFx.Events.Documents.IMessageOutbox.CreateBatch(
        JasperFx.Events.Documents.IDocumentSessionOperations session)
        => new(NewBatch());

    /// <summary>
    /// Optional read of the <em>committed</em> state, run at each commit hook so a suite can tell
    /// which side of the commit that hook is on. Set by the suite before the act, and handed to
    /// every batch this outbox creates from then on.
    /// </summary>
    /// <remarks>
    /// Recording the hook order alone does not prove the boundary — the two would fire in that
    /// order even if both ran before the commit, or both after. What distinguishes them is what the
    /// rest of the database can see at the moment each runs, which is exactly what an outbox's two
    /// delivery guarantees rest on.
    /// </remarks>
    public Func<Task<bool>>? Probe { get; set; }

    /// <summary>
    /// Every batch the store has asked for, in order.
    /// </summary>
    public IReadOnlyList<RecordingMessageBatch> Batches
    {
        get { lock (_locker) { return _batches.ToArray(); } }
    }

    /// <summary>
    /// How many times the store asked for a batch. Zero is a real assertion: a unit of work that
    /// publishes nothing must not reach the outbox at all.
    /// </summary>
    public int BatchCount
    {
        get { lock (_locker) { return _batches.Count; } }
    }

    /// <summary>
    /// Every message published through every batch. The load-bearing count — a store that ignored
    /// <c>RaiseSideEffects</c> entirely leaves this empty while passing every other fact.
    /// </summary>
    public IReadOnlyList<object> PublishedMessages
        => Batches.SelectMany(x => x.Published).ToArray();

    /// <summary>
    /// Every message published through every batch, paired with the tenant id the store handed the
    /// sink alongside it (jasperfx#898).
    /// </summary>
    /// <remarks>
    /// The tenant argument was received and discarded until the conjoined-tenancy fact needed it, and
    /// discarding it is exactly the shape of bug worth pinning: a projection's side effects are what
    /// leave the store's transactional boundary, so a message published under the wrong tenant — or
    /// under none — is routed, handled and persisted somewhere else entirely, and nothing inside the
    /// store ever notices.
    /// </remarks>
    public IReadOnlyList<(object Message, string TenantId)> PublishedWithTenant
        => Batches.SelectMany(x => x.PublishedWithTenant).ToArray();

    /// <summary>
    /// Batches that fired at least one commit hook, which is not the same set as
    /// <see cref="Batches" />: a unit of work that failed before the commit boundary produces a
    /// batch that never reaches either hook.
    /// </summary>
    public IReadOnlyList<RecordingMessageBatch> CommittedBatches
        => Batches.Where(x => x.Hooks.Count > 0).ToArray();

    /// <summary>
    /// Create and record a batch. Called from each consumer's own <c>CreateBatch</c>.
    /// </summary>
    protected RecordingMessageBatch NewBatch()
    {
        var batch = new RecordingMessageBatch(Probe);

        lock (_locker)
        {
            _batches.Add(batch);
        }

        return batch;
    }

    /// <summary>
    /// Drop every batch and clear the probe. Called between arrange and act so a fact asserts on
    /// the round it is actually testing — the outbox instance is shared across the suite's tests,
    /// because the configuration delegate is what the fixture keys a store rebuild on.
    /// </summary>
    public void Reset()
    {
        lock (_locker)
        {
            _batches.Clear();
        }

        Probe = null;
    }
}

/// <summary>
/// One outbox batch — the messages published through it and the order its commit hooks fired in.
/// </summary>
/// <inheritdoc cref="RecordingMessageOutbox" path="/remarks"/>
public partial class RecordingMessageBatch : JasperFx.Events.Documents.IMessageBatch
{
    private readonly object _locker = new();
    private readonly List<(object Message, string TenantId)> _published = new();
    private readonly List<string> _hooks = new();
    private readonly Func<Task<bool>>? _probe;

    internal RecordingMessageBatch(Func<Task<bool>>? probe) => _probe = probe;

    Task JasperFx.Events.Documents.IMessageBatch.BeforeCommitAsync(CancellationToken token)
        => RecordBeforeCommitAsync();

    Task JasperFx.Events.Documents.IMessageBatch.AfterCommitAsync(CancellationToken token)
        => RecordAfterCommitAsync();

    /// <summary>
    /// Messages published through this batch, in order.
    /// </summary>
    public IReadOnlyList<object> Published
    {
        get { lock (_locker) { return _published.Select(x => x.Message).ToArray(); } }
    }

    /// <inheritdoc cref="RecordingMessageOutbox.PublishedWithTenant" />
    public IReadOnlyList<(object Message, string TenantId)> PublishedWithTenant
    {
        get { lock (_locker) { return _published.ToArray(); } }
    }

    /// <summary>
    /// The hooks that fired, in the order they fired — <c>"before"</c> then <c>"after"</c>.
    /// </summary>
    public IReadOnlyList<string> Hooks
    {
        get { lock (_locker) { return _hooks.ToArray(); } }
    }

    /// <summary>
    /// What <see cref="RecordingMessageOutbox.Probe" /> saw when the before-commit hook ran. Null
    /// when no probe was set, or when the hook never fired.
    /// </summary>
    public bool? VisibleAtBeforeCommit { get; private set; }

    /// <summary>
    /// What <see cref="RecordingMessageOutbox.Probe" /> saw when the after-commit hook ran.
    /// </summary>
    public bool? VisibleAtAfterCommit { get; private set; }

    /// <summary>
    /// The <c>IMessageSink</c> half of the contract, which is genuinely shared
    /// (<see cref="IMessageSink" /> lives in <c>JasperFx.Events</c>), so the library implements it
    /// rather than pushing it onto the consumer's partial. The metadata overload is left to the
    /// interface's own default, which forwards here.
    /// </summary>
    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        lock (_locker)
        {
            _published.Add((message!, tenantId));
        }

        return new ValueTask();
    }

    /// <summary>
    /// Called by each consumer's partial from its own before-commit hook.
    /// </summary>
    protected async Task RecordBeforeCommitAsync()
    {
        if (_probe != null)
        {
            VisibleAtBeforeCommit = await _probe().ConfigureAwait(false);
        }

        lock (_locker)
        {
            _hooks.Add("before");
        }
    }

    /// <summary>
    /// Called by each consumer's partial from its own after-commit hook.
    /// </summary>
    protected async Task RecordAfterCommitAsync()
    {
        if (_probe != null)
        {
            VisibleAtAfterCommit = await _probe().ConfigureAwait(false);
        }

        lock (_locker)
        {
            _hooks.Add("after");
        }
    }
}
