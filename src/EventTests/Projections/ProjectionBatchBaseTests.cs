using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#1045. Everything here is what PolecatProjectionBatch and FisherProjectionBatch carried twice. The
// store half is faked as a list of what happened and in what order, because the order is most of the contract.
public class ProjectionBatchBaseTests
{
    private readonly RecordingOutbox theOutbox = new();

    private FakeProjectionBatch aBatch(ShardExecutionMode mode = ShardExecutionMode.Continuous)
        => new(theOutbox, mode);

    private static StreamAction aStreamAction(Guid? id = null)
        => new StreamAction(id ?? Guid.NewGuid(), StreamActionType.Append).AddEvent(new Event<object>(new object()));

    [Fact]
    public void session_for_tenant_opens_a_session_and_collects_it()
    {
        var batch = aBatch();

        var session = batch.SessionForTenant("tenant-a");

        batch.OpenedFor.ShouldBe(["tenant-a"]);
        batch.CollectedSessions.ShouldBe([session]);
    }

    [Fact]
    public void the_batch_satisfies_the_store_facing_interface_through_what_it_inherits()
    {
        IProjectionBatch<IFakeSession, IDocumentReadOperations> batch = aBatch();

        batch.SessionForTenant("tenant-a").ShouldNotBeNull();
    }

    [Fact]
    public void a_registered_session_joins_the_batch_without_being_opened_by_it()
    {
        var batch = aBatch();
        var session = Substitute.For<IFakeSession>();

        batch.RegisterSession(session);

        batch.OpenedFor.ShouldBeEmpty();
        batch.CollectedSessions.ShouldBe([session]);
    }

    [Fact]
    public async Task collecting_sessions_concurrently_loses_none()
    {
        var batch = aBatch();

        await Parallel.ForAsync(0, 200, (i, _) =>
        {
            batch.SessionForTenant("tenant-" + i);
            return ValueTask.CompletedTask;
        });

        batch.CollectedSessions.Count.ShouldBe(200);
    }

    [Fact]
    public async Task disposing_disposes_every_session_exactly_once()
    {
        var batch = aBatch();
        var one = batch.SessionForTenant("one");
        var two = batch.SessionForTenant("two");

        await batch.DisposeAsync();
        await batch.DisposeAsync();

        await one.Received(1).DisposeAsync();
        await two.Received(1).DisposeAsync();
    }

    // The three members all funnel the same StreamAction in, and the single-stream-start path really does hand
    // over one instance several times: an append per call would write every event three times (polecat#420).
    [Fact]
    public async Task one_action_arriving_through_all_three_members_is_planned_once()
    {
        var batch = aBatch();
        var action = aStreamAction();

        batch.QuickAppendEventWithVersion(action, action.Events[0]);
        batch.QuickAppendEventWithVersion(action, action.Events[0]);
        batch.UpdateStreamVersion(action);
        batch.QuickAppendEvents(action);

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Planned.ShouldHaveSingleItem().ShouldBe([action]);
    }

    [Fact]
    public async Task two_actions_for_one_stream_are_two_real_appends()
    {
        var batch = aBatch();
        var streamId = Guid.NewGuid();
        var first = aStreamAction(streamId);
        var second = aStreamAction(streamId);

        batch.QuickAppendEvents(first);
        batch.QuickAppendEvents(second);

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Planned.ShouldHaveSingleItem().ShouldBe([first, second]);
    }

    [Fact]
    public async Task actions_raised_from_concurrent_slices_are_all_recorded_once()
    {
        var batch = aBatch();
        var actions = Enumerable.Range(0, 100).Select(_ => aStreamAction()).ToArray();

        await Parallel.ForAsync(0, 400, (i, _) =>
        {
            batch.QuickAppendEvents(actions[i % actions.Length]);
            return ValueTask.CompletedTask;
        });

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Planned.ShouldHaveSingleItem().ShouldBe(actions, ignoreOrder: true);
    }

    [Fact]
    public async Task nothing_is_planned_when_nothing_was_raised()
    {
        var batch = aBatch();

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Planned.ShouldBeEmpty();
    }

    // Polecat opens a session per tenant inside the plan, and relies on it being swept up with the others.
    [Fact]
    public async Task planning_runs_before_the_transaction_so_a_session_it_opens_is_collected()
    {
        var batch = aBatch();
        batch.OnPlan = () => batch.SessionForTenant("raised-tenant");
        batch.QuickAppendEvents(aStreamAction());

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Timeline.IndexOf("plan").ShouldBeLessThan(batch.Timeline.IndexOf("transaction"));
        batch.SessionsSeenByTransaction.ShouldBe(1);
    }

    // Fisher's shape: the read has to happen under the write lock, so it plans inside the transaction, and a
    // SQLITE_BUSY retry must find the same list (fisher#12).
    [Fact]
    public async Task raised_events_can_be_read_again_on_every_transaction_attempt()
    {
        var batch = aBatch();
        var action = aStreamAction();
        batch.QuickAppendEvents(action);
        batch.Attempts = 2;

        await batch.ExecuteAsync(CancellationToken.None);

        batch.RaisedSeenByAttempt.Count.ShouldBe(2);
        batch.RaisedSeenByAttempt.ShouldAllBe(x => x.Single() == action);
    }

    [Fact]
    public async Task a_batch_that_publishes_nothing_never_asks_the_outbox_and_fires_no_hooks()
    {
        var batch = aBatch();

        await batch.ExecuteAsync(CancellationToken.None);

        theOutbox.Created.ShouldBe(0);
        theOutbox.Batch.BeforeCommits.ShouldBe(0);
        theOutbox.Batch.AfterCommits.ShouldBe(0);
        batch.StoreAfterCommits.ShouldBe(1);
    }

    [Fact]
    public async Task publishing_reaches_the_batch_closed_over_the_runtime_type()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");

        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");

        theOutbox.Batch.ClosedOver.ShouldBe([typeof(ShipOrder)]);
        theOutbox.Batch.Tenants.ShouldBe(["tenant-a"]);
    }

    [Fact]
    public async Task publishing_with_metadata_reaches_the_batch_with_the_metadata_intact()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");

        await batch.PublishMessageAsync(new ShipOrder(), new MessageMetadata("tenant-b") { CorrelationId = "c-1" });

        theOutbox.Batch.ClosedOver.ShouldBe([typeof(ShipOrder)]);
        theOutbox.Batch.Tenants.ShouldBe(["tenant-b"]);
        theOutbox.Batch.CorrelationIds.ShouldBe(["c-1"]);
    }

    [Fact]
    public async Task the_outbox_is_handed_the_first_session_the_batch_collected()
    {
        var batch = aBatch();
        var first = batch.SessionForTenant("one");
        batch.SessionForTenant("two");

        await batch.PublishMessageAsync(new ShipOrder(), "one");

        theOutbox.Session.ShouldBeSameAs(first);
    }

    // Polecat threw InvalidOperationException here. A projection publishing without writing a document is
    // ordinary, and failing the shard for it would stop every other projection's page with it.
    [Fact]
    public async Task publishing_from_a_batch_with_no_session_opens_one_rather_than_failing()
    {
        var batch = aBatch();

        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");

        batch.OpenedFor.ShouldBe([StorageConstants.DefaultTenantId]);
        theOutbox.Session.ShouldBeSameAs(batch.CollectedSessions.Single());
    }

    [Fact]
    public async Task concurrent_publishers_share_one_batch_and_lose_nothing()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");

        // Held open until the callers have piled up on the gate, or this could pass by never overlapping
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theOutbox.Hold = release.Task;

        var publishers = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => batch.PublishMessageAsync(new ShipOrder(), "tenant-a")))
            .ToArray();

        await theOutbox.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        release.SetResult();

        await Task.WhenAll(publishers).WaitAsync(TimeSpan.FromSeconds(10));

        theOutbox.Created.ShouldBe(1);
        theOutbox.Batch.ClosedOver.Count.ShouldBe(50);
    }

    // marten#4727: a return taken on the double check leaked the semaphore. The same family of bug on the failure
    // path would wedge every later publish in the batch, not merely fail one.
    [Fact]
    public async Task an_outbox_that_fails_does_not_wedge_the_publishes_after_it()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");
        theOutbox.FailNext = true;

        await Should.ThrowAsync<DivideByZeroException>(() => batch.PublishMessageAsync(new ShipOrder(), "tenant-a"));

        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a").WaitAsync(TimeSpan.FromSeconds(10));

        theOutbox.Batch.ClosedOver.Count.ShouldBe(1);
    }

    [Fact]
    public async Task the_hooks_bracket_the_commit_in_order()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");
        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");

        theOutbox.Batch.Timeline = batch.Timeline;

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Timeline.ShouldBe([
            "transaction",
            "participants",
            "batch-before-commit",
            "commit",
            "batch-after-commit",
            "store-after-commit"
        ]);
    }

    // A retry rolled back whatever the first attempt's before hook wrote, so it has to run again, while the
    // after hook is told once about the one commit that landed.
    [Fact]
    public async Task a_retried_transaction_fires_the_before_hook_per_attempt_and_the_after_hooks_once()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");
        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");
        batch.Attempts = 3;

        await batch.ExecuteAsync(CancellationToken.None);

        theOutbox.Batch.BeforeCommits.ShouldBe(3);
        theOutbox.Batch.AfterCommits.ShouldBe(1);
        batch.StoreAfterCommits.ShouldBe(1);
    }

    [Fact]
    public async Task a_transaction_that_fails_fires_no_after_commit_hook()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");
        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");
        batch.FailTransaction = true;

        await Should.ThrowAsync<DivideByZeroException>(() => batch.ExecuteAsync(CancellationToken.None));

        theOutbox.Batch.AfterCommits.ShouldBe(0);
        batch.StoreAfterCommits.ShouldBe(0);
    }

    // jasperfx#953: a drain that times out cancels the token ExecuteAsync received, and it can land after the
    // commit. The page will never be reprocessed, so a hook handed that token abandons committed work for good.
    [Fact]
    public async Task a_drain_landing_after_the_commit_does_not_reach_the_after_commit_hook()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");
        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");

        using var drain = new CancellationTokenSource();
        batch.OnCommit = drain.Cancel;

        await batch.ExecuteAsync(drain.Token);

        drain.IsCancellationRequested.ShouldBeTrue();
        theOutbox.Batch.AfterCommitToken.CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public async Task the_before_hook_runs_on_the_token_the_transaction_is_using()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");
        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");

        using var source = new CancellationTokenSource();

        await batch.ExecuteAsync(source.Token);

        theOutbox.Batch.BeforeCommitToken.ShouldBe(source.Token);
    }

    // The write landed, so a participant waiting to hear that should hear it whatever the flush did.
    [Fact]
    public async Task a_failed_flush_still_tells_the_store_and_still_fails_the_batch()
    {
        var batch = aBatch();
        batch.SessionForTenant("tenant-a");
        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");
        theOutbox.Batch.FailAfterCommit = true;

        await Should.ThrowAsync<DivideByZeroException>(() => batch.ExecuteAsync(CancellationToken.None));

        batch.StoreAfterCommits.ShouldBe(1);
    }

    // The behaviour Polecat lacked. Its message hooks fired whatever the mode, so anything reaching the batch
    // during a rebuild went out, and a rebuild is the whole event history. The runner normally stops this
    // first, but marten#4729 is what it looks like when that gate is missed.
    [Theory]
    [InlineData(ShardExecutionMode.Rebuild)]
    [InlineData(ShardExecutionMode.CatchUp)]
    public async Task a_replay_never_reaches_the_outbox(ShardExecutionMode mode)
    {
        var batch = aBatch(mode);
        batch.SessionForTenant("tenant-a");

        await batch.PublishMessageAsync(new ShipOrder(), "tenant-a");
        await batch.PublishMessageAsync(new ShipOrder(), new MessageMetadata("tenant-a"));
        await batch.ExecuteAsync(CancellationToken.None);

        theOutbox.Created.ShouldBe(0);
        theOutbox.Batch.ClosedOver.ShouldBeEmpty();
        theOutbox.Batch.BeforeCommits.ShouldBe(0);
        theOutbox.Batch.AfterCommits.ShouldBe(0);
    }

    // Worse than a repeated message, because it is permanent: the event lands on the stream again, takes a new
    // sequence, and the next rebuild has one more event to replay and raise from.
    [Theory]
    [InlineData(ShardExecutionMode.Rebuild)]
    [InlineData(ShardExecutionMode.CatchUp)]
    public async Task a_replay_raises_no_events(ShardExecutionMode mode)
    {
        var batch = aBatch(mode);
        var action = aStreamAction();

        batch.QuickAppendEventWithVersion(action, action.Events[0]);
        batch.UpdateStreamVersion(action);
        batch.QuickAppendEvents(action);

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Planned.ShouldBeEmpty();
        batch.RaisedSeenByAttempt.ShouldHaveSingleItem().ShouldBeEmpty();
    }

    // The batch's writes are what a rebuild is for, so the gate must only ever cover what leaves the database.
    [Theory]
    [InlineData(ShardExecutionMode.Rebuild)]
    [InlineData(ShardExecutionMode.CatchUp)]
    public async Task a_replay_still_commits_the_batch(ShardExecutionMode mode)
    {
        var batch = aBatch(mode);

        await batch.ExecuteAsync(CancellationToken.None);

        batch.Timeline.ShouldContain("commit");
    }

    [Fact]
    public void the_mode_is_available_to_a_stores_own_listeners()
    {
        aBatch(ShardExecutionMode.Continuous).CanRaiseSideEffects.ShouldBeTrue();
        aBatch(ShardExecutionMode.Rebuild).CanRaiseSideEffects.ShouldBeFalse();
        aBatch(ShardExecutionMode.CatchUp).CanRaiseSideEffects.ShouldBeFalse();
    }

    public record ShipOrder;

    public interface IFakeSession : IDocumentSessionOperations, IStorageOperations;

    private class FakeProjectionBatch(IMessageOutbox outbox, ShardExecutionMode mode)
        : ProjectionBatchBase<IFakeSession>(mode), IProjectionBatch<IFakeSession, IDocumentReadOperations>
    {
        public List<string> OpenedFor { get; } = new();
        public List<string> Timeline { get; } = new();
        public List<IReadOnlyList<StreamAction>> Planned { get; } = new();
        public List<IReadOnlyList<StreamAction>> RaisedSeenByAttempt { get; } = new();

        public int Attempts { get; set; } = 1;
        public bool FailTransaction { get; set; }
        public Action? OnPlan { get; set; }
        public Action? OnCommit { get; set; }
        public int SessionsSeenByTransaction { get; private set; }
        public int StoreAfterCommits { get; private set; }

        public IReadOnlyCollection<IFakeSession> CollectedSessions => Sessions;
        public bool CanRaiseSideEffects => SideEffectsEnabled;

        protected override IMessageOutbox Outbox => outbox;

        protected override IFakeSession OpenSessionForTenant(string tenantId)
        {
            lock (OpenedFor)
            {
                OpenedFor.Add(tenantId);
            }

            return Substitute.For<IFakeSession>();
        }

        public override ValueTask RecordProgress(EventRange range) => ValueTask.CompletedTask;

        protected override Task PlanRaisedEventsAsync(IReadOnlyList<StreamAction> raisedEvents, CancellationToken token)
        {
            Timeline.Add("plan");
            Planned.Add(raisedEvents);
            OnPlan?.Invoke();
            return Task.CompletedTask;
        }

        protected override async Task ExecuteOperationsInTransactionAsync(Func<CancellationToken, Task> beforeCommit,
            CancellationToken token)
        {
            Timeline.Add("transaction");
            SessionsSeenByTransaction = Sessions.Count;

            // What a resilience pipeline does to a store's delegate: the whole thing again
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                RaisedSeenByAttempt.Add(RaisedEvents);

                if (FailTransaction)
                {
                    throw new DivideByZeroException();
                }

                if (attempt == Attempts)
                {
                    Timeline.Add("participants");
                }

                await beforeCommit(token);

                if (attempt == Attempts)
                {
                    Timeline.Add("commit");
                    OnCommit?.Invoke();
                }
            }
        }

        protected override Task AfterCommitAsync()
        {
            StoreAfterCommits++;
            Timeline.Add("store-after-commit");
            return Task.CompletedTask;
        }
    }

    private class RecordingOutbox : IMessageOutbox
    {
        private int _created;

        public RecordingMessageBatch Batch { get; } = new();
        public IDocumentSessionOperations? Session { get; private set; }
        public int Created => _created;
        public bool FailNext { get; set; }
        public Task? Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IMessageBatch> CreateBatch(IDocumentSessionOperations session)
        {
            Entered.TrySetResult();

            if (Hold is not null)
            {
                await Hold;
            }

            if (FailNext)
            {
                FailNext = false;
                throw new DivideByZeroException();
            }

            Interlocked.Increment(ref _created);
            Session = session;
            return Batch;
        }
    }

    private class RecordingMessageBatch : IMessageBatch
    {
        public List<string>? Timeline { get; set; }
        public List<Type> ClosedOver { get; } = new();
        public List<string> Tenants { get; } = new();
        public List<string?> CorrelationIds { get; } = new();
        public int BeforeCommits { get; private set; }
        public int AfterCommits { get; private set; }
        public CancellationToken BeforeCommitToken { get; private set; }
        public CancellationToken AfterCommitToken { get; private set; }
        public bool FailAfterCommit { get; set; }

        public ValueTask PublishAsync<T>(T message, string tenantId)
        {
            lock (ClosedOver)
            {
                ClosedOver.Add(typeof(T));
                Tenants.Add(tenantId);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
        {
            lock (ClosedOver)
            {
                ClosedOver.Add(typeof(T));
                Tenants.Add(metadata.TenantId);
                CorrelationIds.Add(metadata.CorrelationId);
            }

            return ValueTask.CompletedTask;
        }

        public Task BeforeCommitAsync(CancellationToken token)
        {
            BeforeCommits++;
            BeforeCommitToken = token;
            Timeline?.Add("batch-before-commit");
            return Task.CompletedTask;
        }

        public Task AfterCommitAsync(CancellationToken token)
        {
            AfterCommits++;
            AfterCommitToken = token;
            Timeline?.Add("batch-after-commit");

            return FailAfterCommit ? Task.FromException(new DivideByZeroException()) : Task.CompletedTask;
        }
    }
}
