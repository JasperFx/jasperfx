using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventStoreTests.Documents
{
    using InlineProjections;
    using LiveAggregation;

    /// <summary>
    /// jasperfx#964 phase 3: inline projections on the in-memory prototyping store run inside the session's
    /// all-or-nothing commit, so what they write lands -- or fails -- with the events that produced it.
    /// </summary>
    public class InMemoryInlineProjectionTests
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
        public async Task an_inline_snapshot_is_written_in_the_commit_and_kept_current()
        {
            var store = new InMemoryDocumentStore();
            store.Projections.Snapshot<Ticket>(SnapshotLifecycle.Inline);

            var id = await openTicket(store);

            await using (var query = store.QuerySession())
            {
                (await query.LoadAsync<Ticket>(id, Token))!.Title.ShouldBe("printer on fire");
            }

            await using (var session = store.LightweightSession())
            {
                session.Events.Append(id, new TicketAssigned("agent-7"));
                await session.SaveChangesAsync(Token);
            }

            await using var reader = store.QuerySession();
            (await reader.LoadAsync<Ticket>(id, Token))!.AssignedTo.ShouldBe("agent-7");
            (await reader.Events.FetchLatest<Ticket>(id, Token))!.AssignedTo.ShouldBe("agent-7");
        }

        [Fact]
        public async Task an_inline_snapshot_is_deleted_when_the_aggregate_says_so()
        {
            var store = new InMemoryDocumentStore();
            store.Projections.Snapshot<Ticket>(SnapshotLifecycle.Inline);

            var id = await openTicket(store);

            await using (var session = store.LightweightSession())
            {
                session.Events.Append(id, new TicketDeleted());
                await session.SaveChangesAsync(Token);
            }

            await using var query = store.QuerySession();
            (await query.LoadAsync<Ticket>(id, Token)).ShouldBeNull();
        }

        [Fact]
        public async Task a_projection_that_throws_rolls_back_the_events_and_the_documents()
        {
            var store = new InMemoryDocumentStore();
            store.Projections.Snapshot<Ticket>(SnapshotLifecycle.Inline);
            store.Projections.Add(new FailingProjection(), ProjectionLifecycle.Inline);

            var id = Guid.NewGuid();
            var note = new Note { Text = "written alongside the stream" };

            await using (var session = store.LightweightSession())
            {
                session.Store(note);
                session.Events.StartStream<Ticket>(id, new TicketOpened("doomed"), new TicketAssigned("nobody"));

                // The shared projection runtime wraps what the projection threw
                var thrown = await Should.ThrowAsync<ApplyEventException>(() => session.SaveChangesAsync(Token));
                thrown.InnerException.ShouldBeOfType<DivideByZeroException>();
            }

            await using var query = store.QuerySession();
            (await query.LoadAsync<Note>(note.Id, Token)).ShouldBeNull();
            (await query.LoadAsync<Ticket>(id, Token)).ShouldBeNull();
            (await query.Events.FetchStreamAsync(id, token: Token)).ShouldBeEmpty();
            (await query.Events.FetchStreamStateAsync(id, Token)).ShouldBeNull();

            // ...and the store isn't left holding its commit gate
            var next = await openTicket(store);
            (await query.LoadAsync<Ticket>(next, Token)).ShouldNotBeNull();
        }

        [Fact]
        public async Task an_event_projection_stores_documents_in_the_same_commit_and_listeners_see_them()
        {
            var store = new InMemoryDocumentStore();
            store.Projections.Add(new TicketLogProjection(), ProjectionLifecycle.Inline);

            var listener = new RecordingListener();
            store.Listeners.Add(listener);

            var id = await openTicket(store, new TicketAssigned("agent-7"));

            await using var query = store.QuerySession();
            var logs = query.Query<TicketLog>().ToList();
            logs.Count.ShouldBe(2);
            logs.ShouldAllBe(x => x.TicketId == id);

            listener.Inserted.OfType<TicketLog>().Count().ShouldBe(2);
        }

        [Fact(Timeout = 10_000)]
        public async Task an_inline_projection_can_read_events_inside_the_commit()
        {
            // The commit gate is not re-entrant, so a read made from inside the commit has to skip it rather
            // than wait on its own commit forever
            var store = new InMemoryDocumentStore();
            store.Projections.Add(new StreamLengthProjection(), ProjectionLifecycle.Inline);

            var id = await openTicket(store, new TicketAssigned("agent-7"));

            await using var query = store.QuerySession();
            (await query.LoadAsync<StreamLength>(id, Token))!.Events.ShouldBe(2);
        }

        [Fact]
        public void an_async_lifecycle_is_refused()
        {
            var store = new InMemoryDocumentStore();

            Should.Throw<NotSupportedException>(() => store.Projections.Snapshot<Ticket>(SnapshotLifecycle.Async));
            Should.Throw<NotSupportedException>(() =>
                store.Projections.Add(new TicketLogProjection(), ProjectionLifecycle.Async));
        }

        [Fact]
        public async Task an_async_lifecycle_registered_through_the_base_graph_is_refused_at_the_first_commit()
        {
            var store = new InMemoryDocumentStore();
            store.Projections.Add(new TicketLogProjection() as IProjectionSource<IInMemoryDocumentSession, IInMemoryQuerySession>,
                ProjectionLifecycle.Async);

            await Should.ThrowAsync<NotSupportedException>(() => openTicket(store));
        }

        [Fact]
        public async Task projection_storage_is_only_available_while_committing()
        {
            var store = new InMemoryDocumentStore();
            await using var session = store.LightweightSession();

            await Should.ThrowAsync<InvalidOperationException>(() =>
                session.FetchProjectionStorageAsync<Ticket, Guid>("*DEFAULT*", Token));
        }
    }
}

namespace EventStoreTests.Documents.InlineProjections
{
    using LiveAggregation;

    public class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; } = "";
    }

    public class TicketLog
    {
        public Guid Id { get; set; }
        public Guid TicketId { get; set; }
        public string EventType { get; set; } = "";
    }

    public class TicketLogProjection : EventProjection
    {
        public TicketLogProjection()
        {
            IncludeType<TicketOpened>();
            IncludeType<TicketAssigned>();
        }

        public override ValueTask ApplyAsync(IInMemoryDocumentSession operations, IEvent e, CancellationToken cancellation)
        {
            operations.Store(new TicketLog { Id = e.Id, TicketId = e.StreamId, EventType = e.EventTypeName });
            return ValueTask.CompletedTask;
        }
    }

    public class StreamLength
    {
        public Guid Id { get; set; }
        public int Events { get; set; }
    }

    public class StreamLengthProjection : EventProjection
    {
        public StreamLengthProjection()
        {
            IncludeType<TicketOpened>();
            IncludeType<TicketAssigned>();
        }

        public override async ValueTask ApplyAsync(IInMemoryDocumentSession operations, IEvent e,
            CancellationToken cancellation)
        {
            var events = await operations.Events.FetchStreamAsync(e.StreamId, token: cancellation);
            operations.Store(new StreamLength { Id = e.StreamId, Events = events.Count });
        }
    }

    public class FailingProjection : EventProjection
    {
        public FailingProjection() => IncludeType<TicketAssigned>();

        // An explicit ApplyAsync sees every event in the unit, not only the included types
        public override ValueTask ApplyAsync(IInMemoryDocumentSession operations, IEvent e, CancellationToken cancellation)
            => e.Data is TicketAssigned ? throw new DivideByZeroException() : ValueTask.CompletedTask;
    }

    public class RecordingListener : IDocumentCommitListener
    {
        public List<object> Inserted { get; } = new();

        public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet changes,
            CancellationToken token)
        {
            Inserted.AddRange(changes.Inserted);
            return Task.CompletedTask;
        }
    }
}
