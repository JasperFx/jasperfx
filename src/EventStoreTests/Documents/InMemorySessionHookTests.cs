using System.Data.Common;
using JasperFx;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using JasperFx.Metadata;
using Shouldly;

namespace EventStoreTests.Documents
{
    using SessionHooks;

    /// <summary>
    /// jasperfx#1052 on a real commit. <c>DocumentSessionHooksTests</c> pins the sequence on its own; these
    /// pin what only a store can show, above all that after-commit means after a commit.
    /// </summary>
    public class InMemorySessionHookTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        private static async Task seed(InMemoryDocumentStore store, params Ticket[] tickets)
        {
            await using var session = store.LightweightSession();
            session.Store(tickets);
            await session.SaveChangesAsync(Token);
        }

        private static InMemoryDocumentSession hooked(InMemoryDocumentStore store, IDocumentSaveListener listener)
        {
            var session = store.LightweightSession();
            ((IDocumentSessionOperations)session).AddSaveListener(listener);
            return session;
        }

        private static async Task<Ticket?> load(InMemoryDocumentStore store, Guid id, string? tenantId = null)
        {
            await using var session = tenantId == null ? store.LightweightSession() : store.LightweightSession(tenantId);
            return await session.LoadAsync<Ticket>(id, Token);
        }

        [Fact]
        public async Task before_save_runs_before_anything_is_written_and_after_commit_after_it_landed()
        {
            var store = new InMemoryDocumentStore();
            var ticket = new Ticket();
            var listener = new ProbingListener(async _ => await load(store, ticket.Id));

            await using var session = hooked(store, listener);
            session.Store(ticket);
            await session.SaveChangesAsync(Token);

            listener.BeforeSave.ShouldBe([null]);
            listener.AfterCommit.ShouldHaveSingleItem().ShouldNotBeNull().Id.ShouldBe(ticket.Id);
        }

        [Fact]
        public async Task work_enlisted_before_save_commits_in_the_same_unit()
        {
            // What an outbox does in this hook
            var store = new InMemoryDocumentStore();
            var enlisted = new Ticket { Title = "from the hook" };
            var listener = new ProbingListener(onBeforeSave: session => session.Store(enlisted));

            await using var session = hooked(store, listener);
            session.Store(new Ticket());
            await session.SaveChangesAsync(Token);

            (await load(store, enlisted.Id)).ShouldNotBeNull();
            listener.Commits.ShouldHaveSingleItem().Inserted.Count.ShouldBe(2);
        }

        [Fact]
        public async Task after_commit_does_not_fire_for_a_commit_that_rolled_back()
        {
            var store = new InMemoryDocumentStore();
            store.OptimisticConcurrencyTypes.Add(typeof(Versioned));

            var first = new Versioned { Title = "winner" };
            await using (var seeded = store.LightweightSession())
            {
                seeded.Store(first);
                await seeded.SaveChangesAsync(Token);
            }

            var stale = new Versioned { Id = first.Id, Version = Guid.NewGuid() };
            var note = new Ticket();
            var listener = new ProbingListener();

            await using var session = hooked(store, listener);
            session.Store(note);
            session.Store(stale);

            await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync(Token));

            listener.BeforeSaveCount.ShouldBe(1);
            listener.AfterCommitCount.ShouldBe(0);
            (await load(store, note.Id)).ShouldBeNull();
        }

        [Fact]
        public async Task a_failed_save_can_be_saved_again_and_only_then_after_commit_fires()
        {
            var store = new InMemoryDocumentStore();
            var ticket = new Ticket();
            var listener = new ProbingListener(onBeforeSave: _ => throw new DivideByZeroException()) { FailBeforeSaveOnce = true };

            await using var session = hooked(store, listener);
            session.Store(ticket);

            await Should.ThrowAsync<DivideByZeroException>(() => session.SaveChangesAsync(Token));
            listener.AfterCommitCount.ShouldBe(0);
            (await load(store, ticket.Id)).ShouldBeNull();

            await session.SaveChangesAsync(Token);

            listener.AfterCommitCount.ShouldBe(1);
            (await load(store, ticket.Id)).ShouldNotBeNull();
        }

        [Fact]
        public async Task an_empty_unit_of_work_raises_neither_hook()
        {
            var store = new InMemoryDocumentStore();
            var listener = new ProbingListener();

            await using var session = hooked(store, listener);
            await session.SaveChangesAsync(Token);

            listener.BeforeSaveCount.ShouldBe(0);
            listener.AfterCommitCount.ShouldBe(0);
        }

        [Fact]
        public async Task hooks_belong_to_the_one_session_they_were_opened_with()
        {
            var store = new InMemoryDocumentStore();
            var listener = new ProbingListener();
            await using var session = hooked(store, listener);

            await seed(store, new Ticket());

            listener.BeforeSaveCount.ShouldBe(0);
            listener.AfterCommitCount.ShouldBe(0);
        }

        [Fact]
        public async Task the_store_wide_commit_listeners_still_hear_the_same_commit_and_hooks_go_first()
        {
            // A store-wide listener that throws must not be able to starve an outbox of its flush
            var store = new InMemoryDocumentStore();
            var order = new List<string>();
            store.Listeners.Add(new ThrowingCommitListener(order));
            var listener = new ProbingListener(onAfterCommit: () => order.Add("hook"));

            await using var session = hooked(store, listener);
            session.Store(new Ticket());

            await Should.ThrowAsync<DivideByZeroException>(() => session.SaveChangesAsync(Token));

            order.ShouldBe(["hook", "store"]);
        }

        [Fact]
        public async Task the_callers_token_does_not_reach_an_after_commit_hook()
        {
            var store = new InMemoryDocumentStore();
            using var cancellation = new CancellationTokenSource();
            var listener = new ProbingListener();

            await using var session = hooked(store, listener);
            session.Store(new Ticket());
            await session.SaveChangesAsync(cancellation.Token);

            listener.AfterCommitToken.ShouldBe(CancellationToken.None);
        }

        [Fact]
        public async Task a_listener_can_join_a_session_the_application_opened_and_has_already_used()
        {
            // Wolverine's outbox Enroll receives a session it did not open. Polecat's and Fisher's listener
            // lists are fixed at open, which is why theirs has no after-commit hook today
            var store = new InMemoryDocumentStore();
            var ticket = new Ticket();

            await using var session = store.LightweightSession();
            session.Store(ticket);
            await session.SaveChangesAsync(Token);

            var listener = new ProbingListener();
            ((IDocumentSessionOperations)session).AddSaveListener(listener);

            session.Store(new Ticket());
            await session.SaveChangesAsync(Token);

            listener.BeforeSaveCount.ShouldBe(1);
            listener.AfterCommitCount.ShouldBe(1);
        }

        [Fact]
        public async Task a_listener_fires_on_every_save_of_its_session()
        {
            var store = new InMemoryDocumentStore();
            var listener = new ProbingListener();

            await using var session = hooked(store, listener);
            session.Store(new Ticket());
            await session.SaveChangesAsync(Token);
            session.Store(new Ticket());
            await session.SaveChangesAsync(Token);

            listener.AfterCommitCount.ShouldBe(2);
        }

        [Fact]
        public async Task the_session_answers_for_its_tenant_through_the_contract()
        {
            IDocumentSessionFactory store = new InMemoryDocumentStore();

            await using var tenanted = store.LightweightSession("tenant-a");
            await using var untenanted = store.LightweightSession();

            tenanted.MetadataContext.TenantId.ShouldBe("tenant-a");
            untenanted.MetadataContext.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
        }

        [Fact]
        public async Task a_session_with_no_connection_refuses_a_participant_instead_of_dropping_it()
        {
            // The prototyping store has nothing honest to hand a participant. Accepting one would commit the
            // documents and lose the envelope row with no error anywhere
            var store = new InMemoryDocumentStore();
            await using var session = store.LightweightSession();

            var refused = Should.Throw<NotSupportedException>(
                () => ((IDocumentWriteOperations)session).AddTransactionParticipant(new NoOpParticipant()));

            refused.Message.ShouldContain(nameof(InMemoryDocumentSession));
        }

        [Fact]
        public async Task metadata_set_through_the_contract_is_what_the_session_stamps()
        {
            var store = new InMemoryDocumentStore();
            await using var session = store.LightweightSession("tenant-a");
            IDocumentSessionOperations shared = session;

            shared.MetadataContext.CausationId = "cause";
            shared.MetadataContext.CorrelationId = "correlation";
            shared.MetadataContext.CurrentUserName = "jeremy";

            var tracked = new TrackedThing();
            session.Store(tracked);
            await session.SaveChangesAsync(Token);

            tracked.CausationId.ShouldBe("cause");
            tracked.CorrelationId.ShouldBe("correlation");
            tracked.LastModifiedBy.ShouldBe("jeremy");
        }
    }

    /// <summary>
    /// jasperfx#1052: the document operations Wolverine's ops and <c>[DocumentExists]</c> reach for.
    /// </summary>
    public class InMemoryDocumentOperationTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        private static IDocumentSessionOperations open(InMemoryDocumentStore store) => store.LightweightSession();

        [Fact]
        public async Task insert_lands_a_new_document()
        {
            var store = new InMemoryDocumentStore();
            var ticket = new Ticket { Title = "new" };

            await using var session = open(store);
            session.Insert(ticket);
            await session.SaveChangesAsync(Token);

            (await session.LoadAsync<Ticket>(ticket.Id, Token)).ShouldNotBeNull().Title.ShouldBe("new");
        }

        [Fact]
        public async Task insert_of_an_existing_identity_fails_the_commit_and_changes_nothing()
        {
            var store = new InMemoryDocumentStore();
            var original = new Ticket { Title = "original" };
            await using (var seeded = open(store))
            {
                seeded.Store(original);
                await seeded.SaveChangesAsync(Token);
            }

            var note = new Ticket();
            await using var session = open(store);
            session.Insert(note);
            session.Insert(new Ticket { Id = original.Id, Title = "duplicate" });

            var refused = await Should.ThrowAsync<DocumentAlreadyExistsException>(() => session.SaveChangesAsync(Token));

            refused.Id.ShouldBe(original.Id);
            (await session.LoadAsync<Ticket>(original.Id, Token)).ShouldNotBeNull().Title.ShouldBe("original");
            (await session.LoadAsync<Ticket>(note.Id, Token)).ShouldBeNull();
        }

        [Fact]
        public async Task update_overwrites_an_existing_document()
        {
            var store = new InMemoryDocumentStore();
            var ticket = new Ticket { Title = "original" };
            await using var session = open(store);
            session.Store(ticket);
            await session.SaveChangesAsync(Token);

            session.Update(new Ticket { Id = ticket.Id, Title = "updated" });
            await session.SaveChangesAsync(Token);

            (await session.LoadAsync<Ticket>(ticket.Id, Token)).ShouldNotBeNull().Title.ShouldBe("updated");
        }

        [Fact]
        public async Task update_of_a_missing_identity_fails_the_commit_rather_than_creating_it()
        {
            var store = new InMemoryDocumentStore();
            var missing = new Ticket();

            await using var session = open(store);
            session.Update(missing);

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(Token));

            (await session.LoadAsync<Ticket>(missing.Id, Token)).ShouldBeNull();
        }

        [Fact]
        public async Task store_objects_writes_each_document_as_its_own_runtime_type()
        {
            var store = new InMemoryDocumentStore();
            var ticket = new Ticket();
            var tracked = new TrackedThing();

            await using var session = open(store);
            session.StoreObjects(new object[] { ticket, tracked });
            await session.SaveChangesAsync(Token);

            (await session.LoadAsync<Ticket>(ticket.Id, Token)).ShouldNotBeNull();
            (await session.LoadAsync<TrackedThing>(tracked.Id, Token)).ShouldNotBeNull();
        }

        [Fact]
        public async Task check_exists_finds_a_document_by_any_identity_load_resolves()
        {
            var store = new InMemoryDocumentStore();
            var ticket = new Ticket();
            var strong = new StrongTicket { Id = new TicketId(Guid.NewGuid()) };

            await using var session = open(store);
            session.Store(ticket);
            session.Store(strong);
            await session.SaveChangesAsync(Token);

            (await session.CheckExistsAsync<Ticket>(ticket.Id, Token)).ShouldBeTrue();
            (await session.CheckExistsAsync<Ticket>(Guid.NewGuid(), Token)).ShouldBeFalse();
            (await session.CheckExistsAsync<StrongTicket>(strong.Id, Token)).ShouldBeTrue();
            (await session.CheckExistsAsync<StrongTicket>(new TicketId(Guid.NewGuid()), Token)).ShouldBeFalse();
        }

        [Fact]
        public async Task check_exists_does_not_find_what_load_would_not()
        {
            var store = new InMemoryDocumentStore();
            store.SoftDeletedTypes.Add(typeof(SoftTicket));
            store.ConjoinedTypes.Add(typeof(TenantedTicket));

            var deleted = new SoftTicket();
            var tenanted = new TenantedTicket();

            await using var session = store.LightweightSession("tenant-a");
            session.Store(deleted);
            session.Store(tenanted);
            await session.SaveChangesAsync(Token);

            IDocumentSessionOperations shared = session;
            session.Delete(deleted);
            await session.SaveChangesAsync(Token);

            (await shared.CheckExistsAsync<SoftTicket>(deleted.Id, Token)).ShouldBeFalse();
            (await shared.CheckExistsAsync<TenantedTicket>(tenanted.Id, Token)).ShouldBeTrue();

            await using var other = store.LightweightSession("tenant-b");
            (await ((IDocumentReadOperations)other).CheckExistsAsync<TenantedTicket>(tenanted.Id, Token)).ShouldBeFalse();
        }
    }
}

namespace EventStoreTests.Documents.SessionHooks
{
    public class Ticket
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
    }

    public class Versioned : IVersioned
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public Guid Version { get; set; }
    }

    public class SoftTicket : ISoftDeleted
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public bool Deleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }

    public class TenantedTicket
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    public class TrackedThing : ITracked
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string CorrelationId { get; set; } = "";
        public string CausationId { get; set; } = "";
        public string LastModifiedBy { get; set; } = "";
    }

    public readonly record struct TicketId(Guid Value);

    public class StrongTicket
    {
        public TicketId Id { get; set; }
    }

    public class NoOpParticipant : IDbTransactionParticipant
    {
        public Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
            => Task.CompletedTask;
    }

    public class ThrowingCommitListener(List<string> order) : IDocumentCommitListener
    {
        public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit, CancellationToken token)
        {
            order.Add("store");
            throw new DivideByZeroException();
        }
    }

    public class ProbingListener(
        Func<IDocumentSessionOperations, Task<Ticket?>>? probe = null,
        Action<IDocumentSessionOperations>? onBeforeSave = null,
        Action? onAfterCommit = null) : IDocumentSaveListener
    {
        public List<Ticket?> BeforeSave { get; } = new();
        public List<Ticket?> AfterCommit { get; } = new();
        public List<IDocumentChangeSet> Commits { get; } = new();
        public int BeforeSaveCount { get; private set; }
        public int AfterCommitCount { get; private set; }
        public CancellationToken AfterCommitToken { get; private set; } = new(true);
        public bool FailBeforeSaveOnce { get; set; }

        public async Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
        {
            BeforeSaveCount++;
            if (probe != null) BeforeSave.Add(await probe(session));

            if (onBeforeSave == null) return;

            if (FailBeforeSaveOnce && BeforeSaveCount > 1) return;

            onBeforeSave(session);
        }

        public async Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit, CancellationToken token)
        {
            AfterCommitCount++;
            AfterCommitToken = token;
            Commits.Add(commit);
            if (probe != null) AfterCommit.Add(await probe(session));
            onAfterCommit?.Invoke();
        }
    }
}
