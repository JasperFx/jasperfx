using EventStoreTests.Documents.LiveAggregation;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// jasperfx#985: the in-memory prototyping store is an <see cref="IEventStore"/>, so the store-agnostic helpers
/// written against it -- Bobcat's arrange and assert steps first among them -- work on a stub-first app.
/// </summary>
/// <remarks>
/// Each fact goes only through the <see cref="IEventStore"/> contract, the same path Bobcat takes: a session
/// opened through <c>IEventStore&lt;,&gt;.OpenSession</c> on the first of <c>AllDatabases()</c>, appends through
/// <see cref="IEventOperations"/>, reads through <see cref="IEventStore.OpenReadOnlyEventStore()"/>.
/// </remarks>
public class InMemoryIEventStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task appendAsync(IEventStore store, Guid id, params object[] events)
    {
        var typed = (IEventStore<IInMemoryDocumentSession, IInMemoryQuerySession>)store;
        var databases = await store.AllDatabases();

        await using var session = typed.OpenSession(databases[0]);
        IEventOperations operations = session.Events;

        var existing = await store.OpenReadOnlyEventStore().FetchStreamAsync(id, token: Token);
        if (existing.Count == 0) operations.StartStream<Ticket>(id, events);
        else operations.Append(id, events);

        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task the_registration_resolves_the_store_as_the_event_store()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Development)
            .ConfigureServices(services => services.AddInMemoryStoreForPrototyping())
            .Build();

        var store = host.Services.GetRequiredService<IEventStore>();
        store.ShouldBeSameAs(host.Services.GetRequiredService<InMemoryDocumentStore>());

        // One database, so a session can be opened on it
        (await store.AllDatabases()).ShouldHaveSingleItem().ShouldBeOfType<InMemoryEventDatabase>();
        store.DatabaseCardinality.ShouldBe(JasperFx.Descriptors.DatabaseCardinality.Single);
    }

    [Fact]
    public async Task arrange_then_read_back_through_the_contract()
    {
        IEventStore store = new InMemoryDocumentStore();
        var id = Guid.NewGuid();

        await appendAsync(store, id, new TicketOpened("printer on fire"));
        await appendAsync(store, id, new TicketAssigned("agent-7"));

        var events = await store.OpenReadOnlyEventStore().FetchStreamAsync(id, token: Token);
        events.Select(x => x.Data.GetType()).ShouldBe([typeof(TicketOpened), typeof(TicketAssigned)]);

        // The read-only view is the session's IQueryEventStore, as on Marten, so it aggregates directly
        var aggregated = await store.OpenReadOnlyEventStore().ShouldBeAssignableTo<IQueryEventStore>()!
            .AggregateStreamAsync<Ticket>(id, token: Token);
        aggregated!.Title.ShouldBe("printer on fire");
        aggregated.AssignedTo.ShouldBe("agent-7");
    }

    [Fact]
    public async Task query_events_since_a_sequence_floor()
    {
        IEventStore store = new InMemoryDocumentStore();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        await appendAsync(store, one, new TicketOpened("one"));
        var database = (await store.AllDatabases())[0];
        var floor = await database.FetchHighestEventSequenceNumber(Token);
        floor.ShouldBe(1);

        await appendAsync(store, two, new TicketOpened("two"));
        await appendAsync(store, one, new TicketAssigned("agent-1"));

        // Bobcat asks for "everything since" with an unbounded page
        var page = await store.OpenReadOnlyEventStore()
            .QueryEventsAsync(new EventQuery { SequenceFloor = floor + 1, PageSize = int.MaxValue }, Token);

        page.TotalCount.ShouldBe(2);
        page.Events.Select(x => x.Sequence).ShouldBe([2L, 3L]);
        page.Events.Select(x => x.StreamId).ShouldBe([two, one]);
    }

    [Fact]
    public async Task query_events_filters_and_pages()
    {
        var store = new InMemoryDocumentStore();
        IEventStore events = store;
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        await appendAsync(store, one, new TicketOpened("one"));
        await appendAsync(store, two, new TicketOpened("two"));
        await appendAsync(store, one, new TicketAssigned("agent-1"));
        await appendAsync(store, two, new TicketAssigned("agent-2"));
        await appendAsync(store, one, new TicketAssigned("agent-3"));

        var reader = events.OpenReadOnlyEventStore();

        var byStream = await reader.QueryEventsAsync(new EventQuery { StreamId = one.ToString(), PageSize = 100 }, Token);
        byStream.Events.ShouldAllBe(x => x.StreamId == one);
        byStream.TotalCount.ShouldBe(3);

        var assigned = store.Events.EventMappingFor(typeof(TicketAssigned)).EventTypeName;
        var byType = await reader.QueryEventsAsync(
            new EventQuery { EventTypeName = assigned, StreamId = two.ToString(), PageSize = 100 }, Token);
        byType.Events.ShouldHaveSingleItem().Data.ShouldBe(new TicketAssigned("agent-2"));

        var secondPage = await reader.QueryEventsAsync(new EventQuery { PageNumber = 2, PageSize = 2 }, Token);
        secondPage.TotalCount.ShouldBe(5);
        secondPage.Events.Select(x => x.Sequence).ShouldBe([3L, 4L]);
    }

    [Fact]
    public async Task tag_filters_are_refused_rather_than_ignored()
    {
        IEventStore store = new InMemoryDocumentStore();

        await Should.ThrowAsync<NotSupportedException>(() => store.OpenReadOnlyEventStore()
            .QueryEventsAsync(new EventQuery { TagValues = { ["ticket"] = "1" } }, Token));
    }

    [Fact]
    public async Task stream_states_are_queryable_with_the_shared_terminators()
    {
        var store = new InMemoryDocumentStore();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        await appendAsync(store, one, new TicketOpened("one"), new TicketAssigned("agent-1"));
        await appendAsync(store, two, new TicketOpened("two"));

        var states = await ((IEventStore)store).OpenReadOnlyEventStore().QueryStreamStates()
            .Where(x => x.Version == 2)
            .ToListAsync(Token);

        var state = states.ShouldHaveSingleItem();
        state.Id.ShouldBe(one);
        state.AggregateType.ShouldBe(typeof(Ticket));
    }

    [Fact]
    public async Task string_identified_streams_filter_on_the_key()
    {
        var store = new InMemoryDocumentStore();
        store.Events.StreamIdentity = StreamIdentity.AsString;

        await using (var session = ((IEventStore<IInMemoryDocumentSession, IInMemoryQuerySession>)store)
                     .OpenSession(store.Database))
        {
            session.Events.StartStream<Escalation>("ESC-1", new TicketOpened("a"));
            session.Events.StartStream<Escalation>("ESC-2", new TicketOpened("b"));
            await session.SaveChangesAsync(Token);
        }

        var page = await ((IEventStore)store).OpenReadOnlyEventStore()
            .QueryEventsAsync(new EventQuery { StreamId = "ESC-2" }, Token);

        page.Events.ShouldHaveSingleItem().StreamKey.ShouldBe("ESC-2");
    }

    [Fact]
    public async Task projections_are_never_stale_and_there_are_no_async_shards()
    {
        var store = new InMemoryDocumentStore();
        await appendAsync(store, Guid.NewGuid(), new TicketOpened("one"));

        var database = (await ((IEventStore)store).AllDatabases())[0];
        await database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(1));
        (await database.AllProjectionProgress(Token)).ShouldBeEmpty();

        ((IEventStore<IInMemoryDocumentSession, IInMemoryQuerySession>)store).AllShards().ShouldBeEmpty();
    }

    [Fact]
    public async Task the_daemon_is_refused_with_a_message_that_says_why()
    {
        IEventStore store = new InMemoryDocumentStore();

        var ex = await Should.ThrowAsync<NotSupportedException>(async () => await store.BuildProjectionDaemonAsync());
        ex.Message.ShouldContain("in-memory prototyping store");
    }
}
