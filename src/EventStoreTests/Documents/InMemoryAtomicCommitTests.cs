using JasperFx;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using JasperFx.Metadata;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// jasperfx#963: a unit of work on the in-memory prototyping store is all-or-nothing. Specifications
/// that exercise refusals and concurrency conflicts depend on a failed commit leaving nothing behind.
/// </summary>
public class InMemoryAtomicCommitTests
{
    public class Ticket : IVersioned
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public Guid Version { get; set; }
    }

    public class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; } = "";
    }

    private static InMemoryDocumentStore storeWithVersionedTickets()
    {
        var store = new InMemoryDocumentStore();
        store.OptimisticConcurrencyTypes.Add(typeof(Ticket));
        return store;
    }

    private static async Task<Ticket> seed(InMemoryDocumentStore store, string title)
    {
        var ticket = new Ticket { Title = title };
        await using var session = store.LightweightSession();
        session.Store(ticket);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ticket;
    }

    [Fact]
    public async Task a_refused_write_rolls_back_everything_else_in_the_unit()
    {
        var store = storeWithVersionedTickets();
        var ticket = await seed(store, "original");

        // Someone else wins the race
        var winner = await loadTicket(store, ticket.Id);
        winner.Title = "winner";
        await using (var other = store.LightweightSession())
        {
            other.Store(winner);
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Our unit: a new note, then a stale ticket update
        var note = new Note { Text = "should not land" };
        ticket.Title = "stale";

        await using var session = store.LightweightSession();
        session.Store(note);
        session.Store(ticket);

        await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        // Nothing from the failed unit landed -- not even the insert that came before the refusal
        (await loadNote(store, note.Id)).ShouldBeNull();
        (await loadTicket(store, ticket.Id)).Title.ShouldBe("winner");
    }

    [Fact]
    public async Task a_rolled_back_commit_restores_the_versions_it_wrote_back_onto_the_callers_instances()
    {
        var store = storeWithVersionedTickets();
        var first = await seed(store, "first");
        var second = await seed(store, "second");

        // Make `second` stale
        var winner = await loadTicket(store, second.Id);
        winner.Title = "winner";
        await using (var other = store.LightweightSession())
        {
            other.Store(winner);
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var firstVersionBefore = first.Version;

        await using var session = store.LightweightSession();
        session.Store(first);   // succeeds inside the unit and stamps a new version on `first`...
        session.Store(second);  // ...then this one is refused

        await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        // ...so `first` must still carry the version that is actually stored, or its next save is refused
        first.Version.ShouldBe(firstVersionBefore);

        await using var retry = store.LightweightSession();
        first.Title = "retried";
        retry.Store(first);
        await retry.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await loadTicket(store, first.Id)).Title.ShouldBe("retried");
    }

    [Fact]
    public async Task a_failed_commit_raises_no_listener()
    {
        var store = storeWithVersionedTickets();
        var listener = new CountingListener();
        store.Listeners.Add(listener);

        var ticket = await seed(store, "original");
        listener.Commits.ShouldBe(1);

        var winner = await loadTicket(store, ticket.Id);
        await using (var other = store.LightweightSession())
        {
            other.Store(winner);
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        listener.Commits.ShouldBe(2);

        await using var session = store.LightweightSession();
        session.Store(ticket);
        await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        listener.Commits.ShouldBe(2);
    }

    [Fact]
    public async Task concurrent_units_of_work_all_land()
    {
        var store = new InMemoryDocumentStore();

        var notes = Enumerable.Range(0, 200).Select(i => new Note { Text = $"note {i}" }).ToArray();

        await Parallel.ForEachAsync(notes, TestContext.Current.CancellationToken, async (note, token) =>
        {
            await using var session = store.LightweightSession();
            session.Store(note);
            await session.SaveChangesAsync(token);
        });

        await using var query = store.QuerySession();
        query.Query<Note>().Count().ShouldBe(200);
    }

    private static async Task<Ticket> loadTicket(InMemoryDocumentStore store, Guid id)
    {
        await using var session = store.QuerySession();
        return (await session.LoadAsync<Ticket>(id, TestContext.Current.CancellationToken))!;
    }

    private static async Task<Note?> loadNote(InMemoryDocumentStore store, Guid id)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<Note>(id, TestContext.Current.CancellationToken);
    }

    private class CountingListener : IDocumentCommitListener
    {
        public int Commits;

        public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit,
            CancellationToken token)
        {
            Interlocked.Increment(ref Commits);
            return Task.CompletedTask;
        }
    }
}
