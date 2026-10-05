using JasperFx.Events;
using JasperFx.Events.InMemory;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// jasperfx#964 phase 1: events append through the session and land in the same all-or-nothing commit
/// as its documents.
/// </summary>
public class InMemoryEventAppendTests
{
    public record TicketOpened(string Title);
    public record TicketClosed;

    public class Ticket
    {
        public Guid Id { get; set; }
    }

    public class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<Guid> startTicket(InMemoryDocumentStore store)
    {
        var id = Guid.NewGuid();
        await using var session = store.LightweightSession();
        session.Events.StartStream<Ticket>(id, new TicketOpened("first"));
        await session.SaveChangesAsync(Token);
        return id;
    }

    [Fact]
    public async Task a_stale_expected_version_rolls_back_the_documents_in_the_same_unit()
    {
        var store = new InMemoryDocumentStore();
        var id = await startTicket(store);

        var note = new Note();
        await using var session = store.LightweightSession();
        session.Store(note);
        session.Events.Append(id, 5, new TicketClosed());

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(() => session.SaveChangesAsync(Token));

        (await session.LoadAsync<Note>(note.Id, Token)).ShouldBeNull();
        (await session.Events.FetchStreamStateAsync(id, Token))!.Version.ShouldBe(1);
    }

    [Fact]
    public async Task starting_a_stream_that_exists_rolls_back_the_other_streams_in_the_unit()
    {
        var store = new InMemoryDocumentStore();
        var existing = await startTicket(store);
        var sibling = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Events.StartStream<Ticket>(sibling, new TicketOpened("sibling"));
        session.Events.StartStream<Ticket>(existing, new TicketOpened("collision"));

        await Should.ThrowAsync<ExistingStreamIdCollisionException>(() => session.SaveChangesAsync(Token));

        (await session.Events.FetchStreamStateAsync(sibling, Token)).ShouldBeNull();
        (await session.Events.FetchStreamAsync(existing, token: Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task sequences_stay_contiguous_across_a_rolled_back_commit()
    {
        var store = new InMemoryDocumentStore();
        var id = await startTicket(store);

        await using (var failing = store.LightweightSession())
        {
            failing.Events.Append(id, 99, new TicketClosed());
            await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(() => failing.SaveChangesAsync(Token));
        }

        await using (var session = store.LightweightSession())
        {
            session.Events.Append(id, new TicketClosed());
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(id, token: Token);
        events.Select(x => x.Sequence).ShouldBe([1L, 2L]);
        events.Select(x => x.Version).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task append_optimistic_requires_the_stream_and_guards_its_version()
    {
        var store = new InMemoryDocumentStore();

        await using (var session = store.LightweightSession())
        {
            await Should.ThrowAsync<NonExistentStreamException>(
                () => session.Events.AppendOptimistic(Guid.NewGuid(), new TicketClosed()));
        }

        var id = await startTicket(store);

        await using var mine = store.LightweightSession();
        await mine.Events.AppendOptimistic(id, Token, new TicketClosed());

        // Someone else appends first...
        await using (var theirs = store.LightweightSession())
        {
            theirs.Events.Append(id, new TicketClosed());
            await theirs.SaveChangesAsync(Token);
        }

        // ...so my optimistic append is refused
        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(() => mine.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task reads_filter_by_version_and_from_version()
    {
        var store = new InMemoryDocumentStore();
        var id = await startTicket(store);

        await using (var session = store.LightweightSession())
        {
            session.Events.Append(id, new TicketClosed(), new TicketOpened("again"), new TicketClosed());
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        (await query.Events.FetchStreamAsync(id, version: 2, token: Token)).Select(x => x.Version).ShouldBe([1L, 2L]);
        (await query.Events.FetchStreamAsync(id, fromVersion: 3, token: Token)).Select(x => x.Version).ShouldBe([3L, 4L]);

        var first = (await query.Events.FetchStreamAsync(id, token: Token))[0];
        (await query.Events.LoadAsync<TicketOpened>(first.Id, Token))!.Data.Title.ShouldBe("first");
    }

    [Fact]
    public async Task string_identity_streams()
    {
        var store = new InMemoryDocumentStore();
        store.Events.StreamIdentity = StreamIdentity.AsString;

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Ticket>("ticket-1", new TicketOpened("keyed"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var state = await query.Events.FetchStreamStateAsync("ticket-1", Token);
        state!.Key.ShouldBe("ticket-1");
        state.AggregateType.ShouldBe(typeof(Ticket));

        Should.Throw<InvalidOperationException>(() => query.Events.StartStream(Guid.NewGuid(), new TicketClosed()));
    }

    [Fact]
    public async Task metadata_is_recorded_when_enabled()
    {
        var store = new InMemoryDocumentStore();
        store.Events.CorrelationIdEnabled = true;
        store.Events.UserNameEnabled = true;

        var id = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "correlation-1";
            session.CurrentUserName = "agent-7";
            session.Events.StartStream<Ticket>(id, new TicketOpened("with metadata"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var @event = (await query.Events.FetchStreamAsync(id, token: Token)).Single();
        @event.CorrelationId.ShouldBe("correlation-1");
        @event.UserName.ShouldBe("agent-7");
    }

    [Fact]
    public async Task out_of_scope_operations_say_so()
    {
        var store = new InMemoryDocumentStore();
        await using var session = store.LightweightSession();

        Should.Throw<NotSupportedException>(() => session.Events.ArchiveStream(Guid.NewGuid()))
            .Message.ShouldContain("in-memory prototyping store");
    }
}
