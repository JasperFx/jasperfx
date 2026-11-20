using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace InMemoryEventStoreTests;

public record Counted;

public record CountedPoison;

public partial class ParkTally
{
    public static readonly ConcurrentDictionary<Guid, bool> Poisoned = new();

    public Guid Id { get; set; }
    public List<long> Sequences { get; set; } = new();

    public void Apply(IEvent<Counted> e) => Sequences.Add(e.Sequence);

    public void Apply(IEvent<CountedPoison> e)
    {
        if (Poisoned.ContainsKey(e.StreamId)) throw new InvalidOperationException("poisoned");
        Sequences.Add(e.Sequence);
    }
}

/// <summary>
/// marten#6003: the orderings of parking the compliance suite can't time, pinned by driving one shard's execution page
/// by page against the in-memory store.
/// </summary>
public class parking_internals : IAsyncLifetime
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
        theStore.Events.AddEventType(typeof(Counted));
        theStore.Events.AddEventType(typeof(CountedPoison));
        theStore.Projections.Snapshot<ParkTally>(SnapshotLifecycle.Async);
        theStore.ContinuousErrors.ApplyErrors = ProjectionErrorAction.Park;
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
        foreach (var id in thePoisoned) ParkTally.Poisoned.TryRemove(id, out _);
        await theExecution.DisposeAsync();
    }

    private void poison(Guid id)
    {
        thePoisoned.Add(id);
        ParkTally.Poisoned[id] = true;
    }

    private async Task<Guid> appendAsync(Guid? id, params object[] events)
    {
        var stream = id ?? Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        if (id.HasValue) session.Events.Append(stream, events);
        else session.Events.StartStream<ParkTally>(stream, events);
        await session.SaveChangesAsync();
        return stream;
    }

    private async Task<long> processNextPageAsync()
    {
        var page = await theLoader.LoadAsync(new EventRequest
        {
            Floor = theCommitted,
            HighWater = theStore.HighestEventSequence(),
            BatchSize = theShard.Options.BatchSize,
            Name = theShard.Name,
            ErrorOptions = theStore.ContinuousErrors,
            Metrics = theAgent.Metrics
        }, CancellationToken.None);

        await theExecution.ProcessRangeAsync(new EventRange(theAgent, page.Floor, page.Ceiling) { Events = page });
        theCommitted = page.Ceiling;
        return page.Ceiling;
    }

    private async Task processEverythingAsync()
    {
        while (theCommitted < theStore.HighestEventSequence())
        {
            await processNextPageAsync();
        }
    }

    private async Task<ParkTally?> loadAsync(Guid id)
    {
        await using var session = theStore.QuerySession();
        return await session.LoadAsync<ParkTally>(id);
    }

    private Task<IReadOnlyList<ParkedIdentity>> rowsAsync()
        => theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery());

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

    [Fact]
    public async Task nothing_parked_costs_one_read_per_shard_start_and_no_polling()
    {
        for (var i = 0; i < 4; i++) await appendAsync(null, new Counted(), new Counted(), new Counted());
        await processEverythingAsync();

        theDatabase.ParkedLoads.ShouldBe(1);
        theDatabase.RequestedActionLoads.ShouldBe(0);
        theHost.ParkedCount.ShouldBe(0);
        (await theHost.LoadRequestedActionsAsync(CancellationToken.None)).ShouldBeEmpty();
        theDatabase.RequestedActionLoads.ShouldBe(0);
    }

    [Fact]
    public async Task a_park_only_counts_once_its_batch_commits()
    {
        var id = await appendAsync(null, new Counted());
        await processEverythingAsync();

        poison(id);
        await appendAsync(id, new CountedPoison());

        // The progression moves out from under the batch, so it fails at the commit
        await theStore.RewindAgentProgressAsync(theDatabase, theShard.Name.Identity, CancellationToken.None, 1_000_000);
        await processNextPageAsync();

        await theAgent.Received().ReportCriticalFailureAsync(Arg.Any<Exception>());
        (await rowsAsync()).ShouldBeEmpty();
        theHost.TryFindParked(StorageConstants.DefaultTenantId, id, out _).ShouldBeFalse();
        theHost.ParkedCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_failed_replay_leaves_the_page_held_because_the_filter_runs_after_binding()
    {
        var id = await appendAsync(null, new Counted());
        await processEverythingAsync();

        poison(id);
        await appendAsync(id, new CountedPoison());
        await processEverythingAsync();
        theHost.ParkedCount.ShouldBe(1);

        // A page with more of the identity's events, and a replay bound to it that fails again
        await appendAsync(id, new Counted());
        var ticket = await submitAsync(id);
        await processNextPageAsync();

        await Should.ThrowAsync<Exception>(() => ticket.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        (await loadAsync(id))!.Sequences.Count.ShouldBe(1);
        theHost.ParkedCount.ShouldBe(1);
        theHost.ParkedSnapshot().Single().ReplayAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task a_successful_replay_unparks_in_the_batch_it_binds_to()
    {
        var id = await appendAsync(null, new Counted());
        await processEverythingAsync();

        poison(id);
        await appendAsync(id, new CountedPoison());
        await processEverythingAsync();
        await appendAsync(id, new Counted());

        ParkTally.Poisoned.TryRemove(id, out _);
        var ticket = await submitAsync(id);
        await processNextPageAsync();

        var result = await ticket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Unparked.ShouldBeTrue();
        theHost.ParkedCount.ShouldBe(0);
        (await rowsAsync()).ShouldBeEmpty();

        await using var session = theStore.QuerySession();
        (await loadAsync(id))!.Sequences.ShouldBe((await session.Events.FetchStreamAsync(id)).Select(x => x.Sequence));
    }

    [Fact]
    public async Task a_restarted_shard_loads_what_it_parked_before()
    {
        var id = await appendAsync(null, new Counted());
        await processEverythingAsync();

        poison(id);
        await appendAsync(id, new CountedPoison());
        await processEverythingAsync();
        theHost.ParkedCount.ShouldBe(1);

        // A fresh execution for the same shard, as a restart or another node builds
        var again = theShard.Factory.BuildExecution(theStore, theDatabase, NullLogger.Instance, theShard.Name);
        try
        {
            again.TryGetParkingHost(out var host).ShouldBeTrue();
            host!.ParkedCount.ShouldBeNull();

            await appendAsync(id, new Counted());
            var page = await theLoader.LoadAsync(new EventRequest
            {
                Floor = theCommitted,
                HighWater = theStore.HighestEventSequence(),
                BatchSize = theShard.Options.BatchSize,
                Name = theShard.Name,
                ErrorOptions = theStore.ContinuousErrors,
                Metrics = theAgent.Metrics
            }, CancellationToken.None);
            await again.ProcessRangeAsync(new EventRange(theAgent, page.Floor, page.Ceiling) { Events = page });

            host.ParkedCount.ShouldBe(1);
            host.TryFindParked(StorageConstants.DefaultTenantId, id, out _).ShouldBeTrue();
            (await loadAsync(id))!.Sequences.Count.ShouldBe(1);
        }
        finally
        {
            await again.DisposeAsync();
        }
    }

    [Fact]
    public async Task the_default_still_pauses_rather_than_parks()
    {
        theStore.ContinuousErrors.ApplyErrors = ProjectionErrorAction.Pause;

        var id = await appendAsync(null, new Counted());
        await processEverythingAsync();

        poison(id);
        await appendAsync(id, new CountedPoison());
        await processNextPageAsync();

        await theAgent.Received().ReportCriticalFailureAsync(Arg.Any<Exception>());
        (await rowsAsync()).ShouldBeEmpty();
    }
}
