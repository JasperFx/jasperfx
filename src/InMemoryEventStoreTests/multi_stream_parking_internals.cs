using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace InMemoryEventStoreTests;

public record Scored(Guid[] Boards);

public record ScoredPoison(Guid[] Boards);

public class Scoreboard
{
    public static readonly ConcurrentDictionary<Guid, bool> Poisoned = new();

    public Guid Id { get; set; }
    public List<long> Sequences { get; set; } = new();
}

/// <summary>A multi-stream projection grouped by a plain delegate, made findable by its slice index.</summary>
public partial class ScoreboardProjection : MultiStreamProjection<Scoreboard, Guid>
{
    public ScoreboardProjection()
    {
        Name = "Scoreboard";
        Identities<Scored>(x => x.Boards);
        Identities<ScoredPoison>(x => x.Boards);
        Options.UseSliceIndex(SliceIndexRetention.Full);
    }

    public void Apply(IEvent<Scored> e, Scoreboard board) => board.Sequences.Add(e.Sequence);

    public void Apply(IEvent<ScoredPoison> e, Scoreboard board)
    {
        if (Scoreboard.Poisoned.ContainsKey(board.Id)) throw new InvalidOperationException("poisoned");
        board.Sequences.Add(e.Sequence);
    }
}

/// <summary>
/// marten#6003 phase 2: the orderings of a multi-stream identity's park, replay and rebuild that the compliance suite
/// can't time, pinned by driving one shard's execution page by page against the in-memory store. The page a request
/// binds to holds the identity's events here, which is the case where the bound batch has to take them from its own
/// grouping: their slice index rows are only written in that batch.
/// </summary>
public class multi_stream_parking_internals : IAsyncLifetime
{
    private readonly InMemoryDocumentStore theStore = new();
    private ISubscriptionExecution theExecution = null!;
    private IParkedIdentityHost theHost = null!;
    private IIdentityRebuilder theRebuilder = null!;
    private AsyncShard<IInMemoryDocumentSession, IInMemoryQuerySession> theShard = null!;
    private readonly ISubscriptionAgent theAgent = Substitute.For<ISubscriptionAgent>();
    private InMemoryEventDatabase theDatabase = null!;
    private IEventLoader theLoader = null!;
    private long theCommitted;
    private readonly List<Guid> thePoisoned = new();

    public ValueTask InitializeAsync()
    {
        theStore.Events.AddEventType(typeof(Scored));
        theStore.Events.AddEventType(typeof(ScoredPoison));
        theStore.Projections.Add(new ScoreboardProjection(), ProjectionLifecycle.Async);
        theStore.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;
        theShard = theStore.AllShards().Single();
        theShard.Options.BatchSize = 5;

        theDatabase = (InMemoryEventDatabase)theStore.AllDatabases().Result.Single();
        theExecution = theShard.Factory.BuildExecution(theStore, theDatabase, NullLogger.Instance, theShard.Name);
        theExecution.TryGetParkingHost(out var host).ShouldBeTrue();
        theHost = host!;
        theExecution.TryGetIdentityRebuilder(out var rebuilder).ShouldBeTrue();
        theRebuilder = rebuilder!;

        theLoader = theStore.BuildEventLoader(theDatabase, NullLogger.Instance, theShard.Filters, theShard.Options);

        theAgent.Name.Returns(theShard.Name);
        theAgent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in thePoisoned) Scoreboard.Poisoned.TryRemove(id, out _);
        await theExecution.DisposeAsync();
    }

    private void poison(Guid id)
    {
        thePoisoned.Add(id);
        Scoreboard.Poisoned[id] = true;
    }

    private async Task<long> appendAsync(object @event)
    {
        await using var session = theStore.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), @event);
        await session.SaveChangesAsync();
        return theStore.HighestEventSequence();
    }

    private async Task processNextPageAsync()
    {
        var page = await theLoader.LoadAsync(new EventRequest
        {
            Floor = theCommitted,
            HighWater = theStore.HighestEventSequence(),
            BatchSize = theShard.Options.BatchSize,
            Name = theShard.Name,
            ErrorOptions = theStore.Projections.Errors,
            Metrics = theAgent.Metrics
        }, CancellationToken.None);

        await theExecution.ProcessRangeAsync(new EventRange(theAgent, page.Floor, page.Ceiling) { Events = page });
        theCommitted = page.Ceiling;
    }

    private async Task processEverythingAsync()
    {
        while (theCommitted < theStore.HighestEventSequence())
        {
            await processNextPageAsync();
        }
    }

    private async Task<Scoreboard?> loadAsync(Guid id)
    {
        await using var session = theStore.QuerySession();
        return await session.LoadAsync<Scoreboard>(id);
    }

    private async Task<IdentityRebuildTicket> submitAsync(IdentityRebuildRequest request)
    {
        var ticket = new IdentityRebuildTicket(theShard.Name.Name, request);

        var ready = new TaskCompletionSource();
        theRebuilder.Submit(ticket, theCommitted, () =>
        {
            ready.TrySetResult();
            return Task.CompletedTask;
        });

        await Task.WhenAny(ready.Task, ticket.Task).WaitAsync(TimeSpan.FromSeconds(10));
        return ticket;
    }

    private async Task<(Guid Parked, List<long> Expected)> aParkedBoardAsync()
    {
        var board = Guid.NewGuid();
        var expected = new List<long> { await appendAsync(new Scored([board])) };
        await processEverythingAsync();

        poison(board);
        expected.Add(await appendAsync(new ScoredPoison([board])));
        await processEverythingAsync();
        theHost.ParkedCount.ShouldBe(1);

        return (board, expected);
    }

    [Fact]
    public async Task the_index_records_every_identity_of_every_event_in_the_batch_that_applies_it()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var both = await appendAsync(new Scored([first, second]));
        var one = await appendAsync(new Scored([first]));
        await processEverythingAsync();

        theStore.SliceIndexFor("Scoreboard").Select(x => (x.Identity, x.Sequence)).OrderBy(x => x.Sequence)
            .ThenBy(x => x.Identity.ToString())
            .ShouldBe(new[] { (first, both), (second, both), (first, one) }
                .Select(x => ((object)x.Item1, x.Item2)).OrderBy(x => x.Item2).ThenBy(x => x.Item1.ToString()));

        // Where it starts: the shard's first floor
        (await theDatabase.FetchEventRangeMarkAsync(SliceIndexMarks.KeyFor("Scoreboard", 1), null)).ShouldBe(0);
    }

    [Fact]
    public async Task a_parked_identitys_held_events_are_still_indexed()
    {
        var (board, _) = await aParkedBoardAsync();

        var held = await appendAsync(new Scored([board]));
        await processEverythingAsync();

        (await loadAsync(board))!.Sequences.Count.ShouldBe(1);
        theStore.SliceIndexFor("Scoreboard").ShouldContain(x => x.Sequence == held && board.Equals(x.Identity));
    }

    [Fact]
    public async Task an_uncommitted_batch_writes_no_index_rows()
    {
        await appendAsync(new Scored([Guid.NewGuid()]));

        // The progression moves out from under the batch, so it fails at the commit
        await theStore.RewindAgentProgressAsync(theDatabase, theShard.Name.Identity, CancellationToken.None, 1_000_000);
        await processNextPageAsync();

        await theAgent.Received().ReportCriticalFailureAsync(Arg.Any<Exception>());
        theStore.SliceIndexFor("Scoreboard").ShouldBeEmpty();
    }

    [Fact]
    public async Task a_replay_bound_to_a_page_with_the_identitys_events_folds_them_from_the_page()
    {
        var (board, expected) = await aParkedBoardAsync();

        // The page the replay binds to holds more of the board's events, whose index rows aren't committed yet
        expected.Add(await appendAsync(new Scored([board])));
        expected.Add(await appendAsync(new Scored([board, Guid.NewGuid()])));

        Scoreboard.Poisoned.TryRemove(board, out _);
        var ticket = await submitAsync(new IdentityRebuildRequest(StorageConstants.DefaultTenantId, board)
        {
            ResumesParkedIdentity = true
        });
        await processNextPageAsync();

        var result = await ticket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Unparked.ShouldBeTrue();
        theHost.ParkedCount.ShouldBe(0);

        (await loadAsync(board))!.Sequences.ShouldBe(expected);
    }

    [Fact]
    public async Task a_rebuild_from_nothing_bound_to_a_page_with_the_identitys_events_folds_them_from_the_page()
    {
        var board = Guid.NewGuid();
        var expected = new List<long>();
        for (var i = 0; i < 7; i++) expected.Add(await appendAsync(new Scored([board])));
        await processEverythingAsync();

        expected.Add(await appendAsync(new Scored([board])));

        var ticket = await submitAsync(new IdentityRebuildRequest(StorageConstants.DefaultTenantId, board));
        await processNextPageAsync();

        (await ticket.Task.WaitAsync(TimeSpan.FromSeconds(5))).Action.ShouldBe(ActionType.Store);
        (await loadAsync(board))!.Sequences.ShouldBe(expected);

        // Seven events is more than a batch, so they were pre-folded off the loop
        ((AggregationRunner<Scoreboard, Guid, IInMemoryDocumentSession, IInMemoryQuerySession>)theRebuilder)
            .IdentityRebuildPrefolds.ShouldBe(1);
    }

    [Fact]
    public async Task a_resume_fold_that_fails_again_leaves_the_page_held()
    {
        var (board, expected) = await aParkedBoardAsync();

        await appendAsync(new Scored([board]));
        var ticket = await submitAsync(new IdentityRebuildRequest(StorageConstants.DefaultTenantId, board)
        {
            ResumesParkedIdentity = true
        });
        await processNextPageAsync();

        await Should.ThrowAsync<Exception>(() => ticket.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        (await loadAsync(board))!.Sequences.ShouldBe(expected.Take(1));
        theHost.ParkedCount.ShouldBe(1);
        theHost.ParkedSnapshot().Single().ReplayAttempts.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task a_full_rebuild_writes_the_whole_index_again(int flushThreshold)
    {
        var store = new InMemoryDocumentStore();
        store.Events.AddEventType(typeof(Scored));
        var projection = new ScoreboardProjection();
        projection.Options.RebuildFlushThreshold = flushThreshold;
        store.Projections.Add(projection, ProjectionLifecycle.Async);

        var boards = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < 4; i++)
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new Scored(boards), new Scored([boards[i % 3]]));
            await session.SaveChangesAsync();
        }

        var database = (await store.AllDatabases()).Single();

        // As if the index had been turned on late and pruned: it starts above zero and is missing rows
        await database.SetEventRangeMarkAsync(SliceIndexMarks.KeyFor("Scoreboard", 1), null, 5);

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.RebuildProjectionAsync("Scoreboard", TimeSpan.FromSeconds(30), CancellationToken.None);

        (await database.FetchEventRangeMarkAsync(SliceIndexMarks.KeyFor("Scoreboard", 1), null)).ShouldBe(0);

        var index = store.SliceIndexFor("Scoreboard");
        index.Count.ShouldBe(4 * 3 + 4);
        index.Select(x => (x.Identity, x.Sequence)).Distinct().Count().ShouldBe(index.Count);

        await daemon.StopAllAsync();
    }
}
