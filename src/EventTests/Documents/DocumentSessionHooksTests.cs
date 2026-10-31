using System.Data.Common;
using JasperFx.Events;
using JasperFx.Events.Documents;
using NSubstitute;
using Shouldly;

namespace EventTests.Documents;

/// <summary>
/// jasperfx#1052: the sequence around a commit that one shared outbox depends on. The stores' own loops
/// disagree on most of these, so each test names the store whose behaviour would fail it.
/// </summary>
public class DocumentSessionHooksTests
{
    private static readonly DbConnection Connection = Substitute.For<DbConnection>();
    private static readonly DbTransaction Transaction = Substitute.For<DbTransaction>();
    private static readonly IDocumentSessionOperations Session = Substitute.For<IDocumentSessionOperations>();
    private static readonly IDocumentChangeSet Commit = Substitute.For<IDocumentChangeSet>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<string> _log = new();

    private class RecordingParticipant(List<string> log, string name) : IDbTransactionParticipant
    {
        public (DbConnection Connection, DbTransaction Transaction)? Received { get; private set; }

        public Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
        {
            Received = (connection, transaction);
            log.Add(name);
            return Task.CompletedTask;
        }
    }

    private class ThrowingParticipant : IDbTransactionParticipant
    {
        public Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
            => throw new DivideByZeroException();
    }

    private class EnlistingParticipant(DocumentSessionHooks hooks, IDbTransactionParticipant next) : IDbTransactionParticipant
    {
        public Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
        {
            hooks.AddParticipant(next);
            return Task.CompletedTask;
        }
    }

    private class RecordingBatch(List<string> log, Exception? afterCommit = null) : IMessageBatch
    {
        public CancellationToken AfterCommitToken { get; private set; } = new(true);

        public ValueTask PublishAsync<T>(T message, string tenantId) => default;

        public Task BeforeCommitAsync(CancellationToken token)
        {
            log.Add("batch.before");
            return Task.CompletedTask;
        }

        public Task AfterCommitAsync(CancellationToken token)
        {
            AfterCommitToken = token;
            log.Add("batch.after");
            return afterCommit == null ? Task.CompletedTask : Task.FromException(afterCommit);
        }
    }

    private class RecordingListener(List<string> log, string name, Exception? afterCommit = null) : IDocumentSaveListener
    {
        public CancellationToken AfterCommitToken { get; private set; } = new(true);

        public Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
        {
            log.Add($"{name}.before");
            return Task.CompletedTask;
        }

        public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit, CancellationToken token)
        {
            AfterCommitToken = token;
            log.Add($"{name}.after");
            return afterCommit == null ? Task.CompletedTask : Task.FromException(afterCommit);
        }
    }

    private static DocumentSessionHooks hooksWith(params IDocumentSaveListener[] listeners)
    {
        var hooks = new DocumentSessionHooks();
        foreach (var listener in listeners) hooks.AddListener(listener);
        return hooks;
    }

    private static Task beforeCommit(DocumentSessionHooks hooks, IMessageBatch? batch = null)
        => hooks.BeforeCommitAsync(Connection, Transaction, batch, Token);

    private static Task afterCommit(DocumentSessionHooks hooks, IMessageBatch? batch = null)
        => hooks.AfterCommitAsync(Session, Commit, batch);

    [Fact]
    public async Task the_whole_sequence_runs_in_contract_order()
    {
        var hooks = hooksWith(new RecordingListener(_log, "listener"));
        hooks.AddParticipant(new RecordingParticipant(_log, "participant"));
        var batch = new RecordingBatch(_log);

        await hooks.BeforeSaveAsync(Session, Token);
        await beforeCommit(hooks, batch);
        await afterCommit(hooks, batch);

        _log.ShouldBe(["listener.before", "participant", "batch.before", "batch.after", "listener.after"]);
    }

    [Fact]
    public async Task the_message_hook_runs_after_every_participant_however_they_were_enlisted()
    {
        // marten#5353 said "immediately before the COMMIT". Fisher runs the message hook ahead of its
        // participants, and Marten's message participant is overtaken by anything enlisted after the
        // first publish. Here the participants are enlisted last and the batch still goes after them.
        var hooks = new DocumentSessionHooks();
        var batch = new RecordingBatch(_log);
        hooks.AddParticipant(new RecordingParticipant(_log, "first"));
        hooks.AddParticipant(new RecordingParticipant(_log, "second"));

        await beforeCommit(hooks, batch);

        _log.ShouldBe(["first", "second", "batch.before"]);
    }

    [Fact]
    public async Task a_participant_writes_on_the_connection_and_transaction_it_is_handed()
    {
        var hooks = new DocumentSessionHooks();
        var participant = new RecordingParticipant(_log, "participant");
        hooks.AddParticipant(participant);

        await beforeCommit(hooks);

        participant.Received.ShouldBe((Connection, Transaction));
    }

    [Fact]
    public async Task a_participant_that_fails_keeps_the_message_hook_from_running()
    {
        // marten#5353 again: the batch must never be told a transaction is about to commit when a write
        // has already failed
        var hooks = new DocumentSessionHooks();
        hooks.AddParticipant(new ThrowingParticipant());

        await Should.ThrowAsync<DivideByZeroException>(() => beforeCommit(hooks, new RecordingBatch(_log)));

        _log.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_retried_attempt_replays_every_participant_and_the_message_hook()
    {
        // Marten's write pipeline and Fisher's resilience pipeline both re-run this on a fresh transaction,
        // because what the first attempt wrote rolled back with it
        var hooks = new DocumentSessionHooks();
        hooks.AddParticipant(new RecordingParticipant(_log, "participant"));
        var batch = new RecordingBatch(_log);

        await beforeCommit(hooks, batch);
        await beforeCommit(hooks, batch);
        await afterCommit(hooks, batch);

        _log.ShouldBe(["participant", "batch.before", "participant", "batch.before", "batch.after"]);
    }

    [Fact]
    public async Task a_participant_does_not_run_on_the_commit_after_its_own()
    {
        // All three stores keep theirs until the session is disposed, which on a session saved twice
        // inserts the same envelope row twice
        var hooks = new DocumentSessionHooks();
        hooks.AddParticipant(new RecordingParticipant(_log, "participant"));

        await beforeCommit(hooks);
        await afterCommit(hooks);
        _log.Clear();

        await beforeCommit(hooks);

        _log.ShouldBeEmpty();
        hooks.HasPendingParticipants.ShouldBeFalse();
    }

    [Fact]
    public async Task a_rolled_back_commit_leaves_its_participants_waiting_for_the_next_save()
    {
        // The unit of work survives a failed save on every store, so what it enlisted has to as well, or
        // the caller's retry commits the documents and loses the envelope
        var hooks = new DocumentSessionHooks();
        var failing = new ThrowingParticipant();
        hooks.AddParticipant(new RecordingParticipant(_log, "participant"));
        hooks.AddParticipant(failing);

        await Should.ThrowAsync<DivideByZeroException>(() => beforeCommit(hooks));

        hooks.HasPendingParticipants.ShouldBeTrue();
        _log.ShouldBe(["participant"]);
        _log.Clear();

        await Should.ThrowAsync<DivideByZeroException>(() => beforeCommit(hooks));
        _log.ShouldBe(["participant"]);
    }

    [Fact]
    public async Task a_participant_enlisted_during_an_attempt_belongs_to_the_next_commit()
    {
        var hooks = new DocumentSessionHooks();
        var late = new RecordingParticipant(_log, "late");
        hooks.AddParticipant(new EnlistingParticipant(hooks, late));

        await beforeCommit(hooks);
        _log.ShouldBeEmpty();

        await afterCommit(hooks);
        hooks.HasPendingParticipants.ShouldBeTrue();

        await beforeCommit(hooks);
        _log.ShouldBe(["late"]);
    }

    [Fact]
    public async Task a_commit_no_attempt_ran_participants_for_does_not_release_them()
    {
        var hooks = new DocumentSessionHooks();
        hooks.AddParticipant(new RecordingParticipant(_log, "participant"));

        await afterCommit(hooks);

        hooks.HasPendingParticipants.ShouldBeTrue();
    }

    [Fact]
    public void a_pending_participant_is_what_makes_an_otherwise_empty_unit_of_work_real_work()
    {
        // polecat#161 and wolverine#3907: a handler that only cascades a message queues nothing but this
        var hooks = new DocumentSessionHooks();
        hooks.HasPendingParticipants.ShouldBeFalse();

        hooks.AddParticipant(new RecordingParticipant(_log, "participant"));

        hooks.HasPendingParticipants.ShouldBeTrue();
    }

    [Fact]
    public void nulls_are_refused_when_they_are_registered()
    {
        Should.Throw<ArgumentNullException>(() => new DocumentSessionHooks().AddParticipant(null!));
        Should.Throw<ArgumentNullException>(() => new DocumentSessionHooks().AddListener(null!));
    }

    [Fact]
    public async Task a_listener_registering_a_listener_does_not_break_the_save_under_way()
    {
        var hooks = new DocumentSessionHooks();
        var late = new RecordingListener(_log, "late");
        hooks.AddListener(new RegisteringListener(hooks, late));

        await hooks.BeforeSaveAsync(Session, Token);
        _log.ShouldBeEmpty();

        await hooks.BeforeSaveAsync(Session, Token);
        _log.ShouldBe(["late.before"]);
    }

    private class RegisteringListener(DocumentSessionHooks hooks, IDocumentSaveListener next) : IDocumentSaveListener
    {
        private bool _registered;

        public Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
        {
            if (!_registered) hooks.AddListener(next);
            _registered = true;
            return Task.CompletedTask;
        }

        public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit, CancellationToken token)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task enlisting_is_safe_from_concurrent_side_effects()
    {
        // IMessageOutbox.CreateBatch is reachable from the daemon's concurrent slice work
        var hooks = new DocumentSessionHooks();
        var runs = 0;
        var participant = new CountingParticipant(() => Interlocked.Increment(ref runs));

        await Parallel.ForAsync(0, 1000, (_, _) =>
        {
            hooks.AddParticipant(participant);
            return ValueTask.CompletedTask;
        });

        await beforeCommit(hooks);

        runs.ShouldBe(1000);
    }

    private class CountingParticipant(Action onRun) : IDbTransactionParticipant
    {
        public Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
        {
            onRun();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task after_commit_hooks_do_not_see_the_callers_token()
    {
        // Polecat's #783. RequestAborted firing as the response is written would otherwise report a
        // committed unit of work as failed and skip every hook after the first that honoured it. The
        // signature takes no token, so all this can check is what arrives.
        var listener = new RecordingListener(_log, "listener");
        var batch = new RecordingBatch(_log);

        await afterCommit(hooksWith(listener), batch);

        listener.AfterCommitToken.ShouldBe(CancellationToken.None);
        batch.AfterCommitToken.ShouldBe(CancellationToken.None);
    }

    [Fact]
    public async Task one_failing_listener_does_not_keep_the_next_from_flushing()
    {
        // All three stores stop at the first throw, so an application listener registered ahead of an
        // outbox would leave its messages unsent
        var failure = new DivideByZeroException();
        var hooks = hooksWith(
            new RecordingListener(_log, "application", failure),
            new RecordingListener(_log, "outbox"));

        var thrown = await Should.ThrowAsync<DivideByZeroException>(() => afterCommit(hooks));

        thrown.ShouldBeSameAs(failure);
        _log.ShouldBe(["application.after", "outbox.after"]);
    }

    [Fact]
    public async Task a_failing_message_batch_does_not_keep_listeners_from_running()
    {
        var hooks = hooksWith(new RecordingListener(_log, "listener"));

        await Should.ThrowAsync<DivideByZeroException>(
            () => afterCommit(hooks, new RecordingBatch(_log, new DivideByZeroException())));

        _log.ShouldBe(["batch.after", "listener.after"]);
    }

    [Fact]
    public async Task the_message_batch_is_released_before_any_listener_can_fail()
    {
        var hooks = hooksWith(new RecordingListener(_log, "listener", new DivideByZeroException()));

        await Should.ThrowAsync<DivideByZeroException>(() => afterCommit(hooks, new RecordingBatch(_log)));

        _log.ShouldBe(["batch.after", "listener.after"]);
    }

    [Fact]
    public async Task several_after_commit_failures_all_reach_the_caller()
    {
        var hooks = hooksWith(
            new RecordingListener(_log, "first", new DivideByZeroException()),
            new RecordingListener(_log, "second", new InvalidOperationException()));

        var thrown = await Should.ThrowAsync<AggregateException>(() => afterCommit(hooks));

        thrown.InnerExceptions.Select(x => x.GetType())
            .ShouldBe([typeof(DivideByZeroException), typeof(InvalidOperationException)]);
    }

    [Fact]
    public async Task a_before_save_failure_stops_the_listeners_after_it()
    {
        // Unlike after commit nothing has happened yet, so the first failure fails the unit of work
        var hooks = hooksWith(new ThrowingBeforeSave(), new RecordingListener(_log, "never"));

        await Should.ThrowAsync<DivideByZeroException>(() => hooks.BeforeSaveAsync(Session, Token));

        _log.ShouldBeEmpty();
    }

    private class ThrowingBeforeSave : IDocumentSaveListener
    {
        public Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
            => throw new DivideByZeroException();

        public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit, CancellationToken token)
            => Task.CompletedTask;
    }
}
