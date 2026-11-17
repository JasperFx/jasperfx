using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace InMemoryEventStoreTests;

public record Tallied;

public partial class RebuildTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public List<long> Sequences { get; set; } = new();

    public void Apply(IEvent<Tallied> e)
    {
        Count++;
        Sequences.Add(e.Sequence);
    }
}

/// <summary>
/// jasperfx#1201: the orderings the compliance suite cannot time on a real database, pinned by driving one shard's
/// execution page by page against the in-memory store.
/// </summary>
public class identity_rebuild_ordering : IAsyncLifetime
{
    private readonly InMemoryDocumentStore theStore = new();
    private ISubscriptionExecution theExecution = null!;
    private IIdentityRebuilder theRebuilder = null!;
    private AsyncShard<IInMemoryDocumentSession, IInMemoryQuerySession> theShard = null!;
    private readonly ISubscriptionAgent theAgent = Substitute.For<ISubscriptionAgent>();
    private IEventLoader theLoader = null!;
    private long theCommitted;

    public ValueTask InitializeAsync()
    {
        theStore.Events.AddEventType(typeof(Tallied));
        theStore.Projections.Snapshot<RebuildTally>(SnapshotLifecycle.Async);
        theShard = theStore.AllShards().Single();
        theShard.Options.BatchSize = 5;

        var database = theStore.AllDatabases().Result.Single();
        theExecution = theShard.Factory.BuildExecution(theStore, database, NullLogger.Instance, theShard.Name);
        theExecution.TryGetIdentityRebuilder(out var rebuilder).ShouldBeTrue();
        theRebuilder = rebuilder!;

        theLoader = theStore.BuildEventLoader(database, NullLogger.Instance, theShard.Filters, theShard.Options);

        theAgent.Name.Returns(theShard.Name);
        theAgent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await theExecution.DisposeAsync();

    private async Task<Guid> appendAsync(Guid? id, int count)
    {
        var stream = id ?? Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        var events = Enumerable.Range(0, count).Select(_ => (object)new Tallied()).ToArray();
        if (id.HasValue) session.Events.Append(stream, events);
        else session.Events.StartStream<RebuildTally>(stream, events);
        await session.SaveChangesAsync();
        return stream;
    }

    // Load and commit the next page, exactly as the shard would
    private async Task<long> processNextPageAsync()
    {
        var page = await theLoader.LoadAsync(new EventRequest
        {
            Floor = theCommitted,
            HighWater = theStore.HighestEventSequence(),
            BatchSize = theShard.Options.BatchSize,
            Name = theShard.Name,
            ErrorOptions = new ErrorHandlingOptions(),
            Metrics = theAgent.Metrics
        }, CancellationToken.None);

        await processAsync(page);
        return page.Ceiling;
    }

    private async Task processAsync(EventPage page)
    {
        await theExecution.ProcessRangeAsync(new EventRange(theAgent, page.Floor, page.Ceiling) { Events = page });
        theCommitted = page.Ceiling;
    }

    private Task processEmptyPageAsync()
    {
        var page = new EventPage(theCommitted);
        page.CalculateCeiling(theShard.Options.BatchSize, theCommitted);
        return processAsync(page);
    }

    private async Task<IdentityRebuildTicket> submitAsync(Guid id)
    {
        var ticket = new IdentityRebuildTicket(theShard.Name.Name,
            new IdentityRebuildRequest(StorageConstants.DefaultTenantId, id));

        var ready = new TaskCompletionSource();
        theRebuilder.Submit(ticket, theCommitted, () =>
        {
            ready.TrySetResult();
            return Task.CompletedTask;
        });

        await Task.WhenAny(ready.Task, ticket.Task).WaitAsync(TimeSpan.FromSeconds(10));
        return ticket;
    }

    private async Task corruptAsync(Guid id)
    {
        await using var session = theStore.LightweightSession();
        session.Store(new RebuildTally { Id = id, Count = 999, Sequences = [-1] });
        await session.SaveChangesAsync();
    }

    private async Task theTallyMatchesItsStreamAsync(Guid id)
    {
        await using var session = theStore.QuerySession();
        var expected = (await session.Events.FetchStreamAsync(id)).Select(x => x.Sequence).ToList();
        var tally = await session.LoadAsync<RebuildTally>(id);

        tally.ShouldNotBeNull();
        tally.Sequences.ShouldBe(expected);
        tally.Count.ShouldBe(expected.Count);
    }

    private async Task processEverythingAsync()
    {
        while (theCommitted < theStore.HighestEventSequence())
        {
            await processNextPageAsync();
        }
    }

    [Fact]
    public async Task a_request_binds_to_the_next_page_built_and_takes_the_identity_out_of_it()
    {
        var id = await appendAsync(null, 3);
        await processEverythingAsync();

        // In the next page, and in the one after it
        await appendAsync(id, 2);
        await appendAsync(id, 4);

        await corruptAsync(id);
        var ticket = await submitAsync(id);

        var boundCeiling = await processNextPageAsync();

        var result = await ticket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.BoundCeiling.ShouldBe(boundCeiling);
        result.Action.ShouldBe(ActionType.Store);

        // Had the page applied its own events for the identity on top of the corrupted document, the corruption
        // would have survived the rebuild
        await using (var session = theStore.QuerySession())
        {
            (await session.LoadAsync<RebuildTally>(id))!.Count.ShouldBe(
                (await session.Events.FetchStreamAsync(id)).Count(x => x.Sequence <= boundCeiling));
        }

        await processEverythingAsync();
        await theTallyMatchesItsStreamAsync(id);
    }

    [Fact]
    public async Task a_request_bound_to_an_empty_page_completes_at_the_committed_position()
    {
        var id = await appendAsync(null, 3);
        await processEverythingAsync();
        var position = theCommitted;

        await corruptAsync(id);
        var ticket = await submitAsync(id);
        theRebuilder.HasUnboundRequests.ShouldBeTrue();

        await processEmptyPageAsync();

        (await ticket.Task.WaitAsync(TimeSpan.FromSeconds(5))).BoundCeiling.ShouldBe(position);
        theRebuilder.HasUnboundRequests.ShouldBeFalse();
        (await theStore.AllDatabases()).Single().ProjectionProgressFor(theShard.Name).Result.ShouldBe(position);
        await theTallyMatchesItsStreamAsync(id);
    }

    [Fact]
    public async Task two_requests_for_the_same_identity_in_one_batch_share_one_result()
    {
        var id = await appendAsync(null, 3);
        await processEverythingAsync();

        var first = await submitAsync(id);
        var second = await submitAsync(id);

        await processEmptyPageAsync();

        var one = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var two = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        two.ShouldBe(one);
        await theTallyMatchesItsStreamAsync(id);
    }

    [Fact]
    public async Task a_long_stream_is_prefolded_and_the_bound_batch_folds_only_what_came_after()
    {
        // More than BatchSize events, so the stream is pre-folded off the loop
        var id = await appendAsync(null, 12);
        await processEverythingAsync();

        await corruptAsync(id);
        var ticket = await submitAsync(id);

        // Appended between the pre-fold and the batch that binds the request
        await appendAsync(id, 2);
        await processEverythingAsync();

        await ticket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ((AggregationRunner<RebuildTally, Guid, IInMemoryDocumentSession, IInMemoryQuerySession>)
            runnerOf(theExecution)).IdentityRebuildPrefolds.ShouldBe(1);

        await theTallyMatchesItsStreamAsync(id);
    }

    [Fact]
    public async Task a_short_stream_is_folded_inline_without_a_prefold()
    {
        var id = await appendAsync(null, 5);
        await processEverythingAsync();

        var ticket = await submitAsync(id);
        await processEmptyPageAsync();
        await ticket.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ((AggregationRunner<RebuildTally, Guid, IInMemoryDocumentSession, IInMemoryQuerySession>)
            runnerOf(theExecution)).IdentityRebuildPrefolds.ShouldBe(0);
        await theTallyMatchesItsStreamAsync(id);
    }

    [Fact]
    public async Task a_request_that_was_never_bound_is_refused_when_the_shard_stops()
    {
        var id = await appendAsync(null, 3);
        await processEverythingAsync();

        var ticket = await submitAsync(id);
        await theExecution.HardStopAsync();

        var refused = await Should.ThrowAsync<IdentityRebuildRefusedException>(() => ticket.Task);
        refused.Reason.ShouldBe(IdentityRebuildRefusal.ShardStopped);
    }

    [Fact]
    public async Task a_request_that_missed_the_last_page_in_flight_is_carried_once_that_page_commits()
    {
        var id = await appendAsync(null, 3);
        await processEverythingAsync();
        var committed = theCommitted;

        var agent = new SubscriptionAgent(theShard.Name, theShard.Options, TimeProvider.System, theLoader, theExecution,
            new ShardStateTracker(NullLogger.Instance), Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);

        await agent.StartAsync(new SubscriptionExecutionRequest(committed, ShardExecutionMode.Continuous,
            new ErrorHandlingOptions(), new NulloDaemonRuntime()) { StartingHighWater = committed });

        var started = DateTimeOffset.UtcNow.AddSeconds(10);
        while (agent.LastCommitted != committed && DateTimeOffset.UtcNow < started) await Task.Delay(10);
        agent.LastCommitted.ShouldBe(committed);

        // One more page is in flight, and it is already built and committed: it read the pending set before the
        // request arrived, so nothing it does will carry the request
        await appendAsync(Guid.NewGuid(), 1);
        await processNextPageAsync();
        agent.LastEnqueued = theCommitted;

        await corruptAsync(id);
        var rebuilding = agent.RebuildIdentityAsync(new IdentityRebuildRequest(StorageConstants.DefaultTenantId, id),
            CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!theRebuilder.HasUnboundRequests && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        rebuilding.IsCompleted.ShouldBeFalse(rebuilding.Exception?.ToString());
        theRebuilder.HasUnboundRequests.ShouldBeTrue();

        // Only the in-flight page's completion leaves nothing in flight, and the agent then carries the request
        await agent.MarkSuccessAsync(theCommitted);

        (await rebuilding.WaitAsync(TimeSpan.FromSeconds(10))).BoundCeiling.ShouldBe(theCommitted);
        await theTallyMatchesItsStreamAsync(id);

        await agent.DisposeAsync();
    }

    private static object runnerOf(ISubscriptionExecution execution)
        => typeof(GroupedProjectionExecution)
            .GetField("_runner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(execution)!;
}
