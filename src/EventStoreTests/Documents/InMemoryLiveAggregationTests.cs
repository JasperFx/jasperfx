using JasperFx.Events;
using JasperFx.Events.InMemory;
using Shouldly;

namespace EventStoreTests.Documents
{
    using LiveAggregation;

    /// <summary>
    /// jasperfx#964 phase 2: live aggregation on the in-memory prototyping store, folded by the
    /// source-generated Apply / Create dispatchers.
    /// </summary>
    public class InMemoryLiveAggregationTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        private static async Task<Guid> openTicket(InMemoryDocumentStore store, params object[] more)
        {
            var id = Guid.NewGuid();
            await using var session = store.LightweightSession();
            session.Events.StartStream<Ticket>(id, [new TicketOpened("printer on fire"), .. more]);
            await session.SaveChangesAsync(Token);
            return id;
        }

        [Fact]
        public async Task aggregate_stream_folds_the_events_and_sets_the_id()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store, new TicketAssigned("agent-7"));

            await using var session = store.QuerySession();
            var ticket = await session.Events.AggregateStreamAsync<Ticket>(id, token: Token);

            ticket.ShouldNotBeNull();
            ticket.Id.ShouldBe(id);
            ticket.Title.ShouldBe("printer on fire");
            ticket.AssignedTo.ShouldBe("agent-7");
        }

        [Fact]
        public async Task aggregate_stream_to_a_version()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store, new TicketAssigned("agent-7"), new TicketAssigned("agent-9"));

            await using var session = store.QuerySession();
            (await session.Events.AggregateStreamAsync<Ticket>(id, version: 2, token: Token))!.AssignedTo
                .ShouldBe("agent-7");
        }

        [Fact]
        public async Task a_bare_stub_with_no_id_member_still_folds()
        {
            // Design-first: the aggregate is a stub that hasn't grown an Id yet
            var store = new InMemoryDocumentStore();
            var id = Guid.NewGuid();
            await using (var session = store.LightweightSession())
            {
                session.Events.StartStream<Escalation>(id, new TicketOpened("stub"), new TicketAssigned("agent-1"));
                await session.SaveChangesAsync(Token);
            }

            await using var query = store.QuerySession();
            (await query.Events.AggregateStreamAsync<Escalation>(id, token: Token))!.Count.ShouldBe(2);
        }

        [Fact]
        public async Task fetch_for_writing_appends_with_the_folded_version_guarded()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store);

            await using (var session = store.LightweightSession())
            {
                var stream = await session.Events.FetchForWriting<Ticket>(id, Token);
                stream.Aggregate!.Title.ShouldBe("printer on fire");
                stream.CurrentVersion.ShouldBe(1);

                stream.AppendOne(new TicketAssigned("agent-7"));
                await session.SaveChangesAsync(Token);
            }

            await using var query = store.QuerySession();
            (await query.Events.FetchLatest<Ticket>(id, Token))!.AssignedTo.ShouldBe("agent-7");
        }

        [Fact]
        public async Task a_concurrent_append_after_fetch_for_writing_is_refused()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store);

            await using var mine = store.LightweightSession();
            var stream = await mine.Events.FetchForWriting<Ticket>(id, Token);
            stream.AppendOne(new TicketAssigned("mine"));

            await using (var theirs = store.LightweightSession())
            {
                theirs.Events.Append(id, new TicketAssigned("theirs"));
                await theirs.SaveChangesAsync(Token);
            }

            await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(() => mine.SaveChangesAsync(Token));
        }

        [Fact]
        public async Task fetch_for_writing_a_new_stream_starts_it()
        {
            var store = new InMemoryDocumentStore();
            var id = Guid.NewGuid();

            await using (var session = store.LightweightSession())
            {
                var stream = await session.Events.FetchForWriting<Ticket>(id, Token);
                stream.Aggregate.ShouldBeNull();
                stream.AppendOne(new TicketOpened("brand new"));
                await session.SaveChangesAsync(Token);
            }

            await using var query = store.QuerySession();
            var state = await query.Events.FetchStreamStateAsync(id, Token);
            state!.Version.ShouldBe(1);
            state.AggregateType.ShouldBe(typeof(Ticket));
        }

        [Fact]
        public async Task fetch_for_writing_with_a_stale_expected_version_is_refused_up_front()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store);

            await using var session = store.LightweightSession();
            await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
                () => session.Events.FetchForWriting<Ticket>(id, 7, Token));
        }

        [Fact]
        public async Task write_to_aggregate_fetches_decides_and_commits()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store);

            await using (var session = store.LightweightSession())
            {
                await session.Events.WriteToAggregate<Ticket>(id, stream =>
                {
                    if (stream.Aggregate!.AssignedTo is null) stream.AppendOne(new TicketAssigned("agent-3"));
                }, Token);
            }

            await using var query = store.QuerySession();
            (await query.Events.FetchLatest<Ticket>(id, Token))!.AssignedTo.ShouldBe("agent-3");
        }

        [Fact]
        public async Task project_latest_folds_this_sessions_pending_events_on_top()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store);

            await using var session = store.LightweightSession();
            session.Events.Append(id, new TicketAssigned("pending"));

            (await session.Events.ProjectLatest<Ticket>(id, Token))!.AssignedTo.ShouldBe("pending");
            (await session.Events.FetchLatest<Ticket>(id, Token))!.AssignedTo.ShouldBeNull();
        }

        [Fact]
        public async Task the_last_known_aggregate_survives_a_deleting_event()
        {
            var store = new InMemoryDocumentStore();
            var id = await openTicket(store, new TicketAssigned("agent-7"), new TicketDeleted());

            await using var query = store.QuerySession();
            (await query.Events.AggregateStreamAsync<Ticket>(id, token: Token)).ShouldBeNull();
            (await query.Events.AggregateStreamToLastKnownAsync<Ticket>(id, token: Token))!.AssignedTo
                .ShouldBe("agent-7");
        }

        [Fact]
        public async Task string_identity_aggregates()
        {
            var store = new InMemoryDocumentStore();
            store.Events.StreamIdentity = StreamIdentity.AsString;

            await using (var session = store.LightweightSession())
            {
                session.Events.StartStream<Queue>("support", new TicketOpened("one"), new TicketOpened("two"));
                await session.SaveChangesAsync(Token);
            }

            await using var query = store.QuerySession();
            var queue = await query.Events.FetchLatest<Queue>("support", Token);
            queue!.Id.ShouldBe("support");
            queue.Open.ShouldBe(2);
        }

        [Fact]
        public async Task strong_typed_identity()
        {
            var store = new InMemoryDocumentStore();
            var id = new TicketId(Guid.NewGuid());

            await using (var session = store.LightweightSession())
            {
                session.Events.StartStream<TypedTicket>(id.Value, new TicketOpened("typed"));
                await session.SaveChangesAsync(Token);
            }

            await using var query = store.LightweightSession();
            var stream = await query.Events.FetchForWriting<TypedTicket, TicketId>(id, Token);
            stream.Aggregate!.Id.ShouldBe(id);
            stream.Aggregate.Title.ShouldBe("typed");

            (await query.Events.FetchLatest<TypedTicket, TicketId>(id, Token))!.Id.ShouldBe(id);
        }
    }
}

namespace EventStoreTests.Documents.LiveAggregation
{
    public record TicketOpened(string Title);
    public record TicketAssigned(string Agent);
    public record TicketDeleted;

    public class Ticket
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string? AssignedTo { get; set; }

        public static Ticket Create(TicketOpened e) => new() { Title = e.Title };
        public void Apply(TicketAssigned e) => AssignedTo = e.Agent;
        public bool ShouldDelete(TicketDeleted e) => true;
    }

    // A design-first stub: no Id member, just enough to count what happened
    public class Escalation
    {
        public int Count { get; set; }

        public static Escalation Create(TicketOpened e) => new() { Count = 1 };
        public void Apply(TicketAssigned e) => Count++;
    }

    public class Queue
    {
        public string Id { get; set; } = "";
        public int Open { get; set; }

        public static Queue Create(TicketOpened e) => new() { Open = 1 };
        public void Apply(TicketOpened e) => Open++;
    }

    public record struct TicketId(Guid Value);

    public class TypedTicket
    {
        public TicketId Id { get; set; }
        public string Title { get; set; } = "";

        public static TypedTicket Create(TicketOpened e) => new() { Title = e.Title };
    }
}
