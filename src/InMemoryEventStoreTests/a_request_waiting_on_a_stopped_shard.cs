using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using Shouldly;

namespace InMemoryEventStoreTests;

/// <summary>
/// jasperfx#1263: a durable replay or discard request made while no node runs the shard waits for the shard to start.
/// The runner used to load its parked set inside its first batch only, so a shard that restarted with no new events
/// never saw the request. These restart the shard with nothing new to read.
/// </summary>
public class a_request_waiting_on_a_stopped_shard : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly InMemoryDocumentStore theStore = new();
    private readonly List<Guid> thePoisoned = new();
    private InMemoryEventDatabase theDatabase = null!;
    private IProjectionDaemon theDaemon = null!;
    private ShardName theShard = null!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theStore.Events.AddEventType(typeof(Counted));
        theStore.Events.AddEventType(typeof(CountedPoison));
        theStore.Projections.Snapshot<ParkTally>(SnapshotLifecycle.Async);
        theStore.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;
        theShard = theStore.AllShards().Single().Name;

        theDatabase = (InMemoryEventDatabase)(await theStore.AllDatabases()).Single();

        theDaemon = await theStore.BuildProjectionDaemonAsync();
        await theDaemon.StartAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in thePoisoned) ParkTally.Poisoned.TryRemove(id, out _);

        await theDaemon.StopAllAsync();
        theDaemon.Dispose();
    }

    private async Task<Guid> appendAsync(Guid? id, params object[] events)
    {
        var stream = id ?? Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        if (id.HasValue) session.Events.Append(stream, events);
        else session.Events.StartStream<ParkTally>(stream, events);
        await session.SaveChangesAsync(Cancellation);
        return stream;
    }

    private Task<IReadOnlyList<ParkedIdentity>> rowsAsync()
        => theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation);

    // Parks an identity, stops the shard, and asks for the action while nothing runs it
    private async Task<Guid> requestWhileTheShardIsStoppedAsync(ParkedAction action)
    {
        var id = await appendAsync(null, new Counted());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        thePoisoned.Add(id);
        ParkTally.Poisoned[id] = true;
        await appendAsync(id, new CountedPoison());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        (await rowsAsync()).ShouldHaveSingleItem().Identity.ShouldBe(id);

        await theDaemon.StopAgentAsync(theShard);

        var marked = await theDatabase.RequestParkedActionAsync(new ParkedActionRequest
        {
            ProjectionName = theShard.Name,
            Action = action,
            IdentityText = ParkedIdentityText.Format(id)
        }, Cancellation);

        marked.ShouldBe(1);
        (await rowsAsync()).ShouldHaveSingleItem().RequestedAction.ShouldBe(action);

        return id;
    }

    // Started with no new events, so the shard builds no batch of its own
    private async Task restartAndWaitForTheUnparkAsync()
    {
        var highest = theStore.HighestEventSequence();

        var unparked = theDaemon.Tracker.WaitForShardCondition(
            x => x.ShardName == theShard.Identity && x.Action == ShardAction.Unparked,
            $"{theShard.Identity} unparks", Timeout);

        await theDaemon.StartAgentAsync(theShard.Identity, Cancellation);

        var state = await unparked.WaitAsync(Timeout, Cancellation);
        state.ParkedCount.ShouldBe(0);

        theStore.HighestEventSequence().ShouldBe(highest);
    }

    [Fact]
    public async Task a_replay_runs_when_the_shard_restarts_with_no_new_events()
    {
        var id = await requestWhileTheShardIsStoppedAsync(ParkedAction.Replay);
        ParkTally.Poisoned.TryRemove(id, out _);

        await restartAndWaitForTheUnparkAsync();

        (await rowsAsync()).ShouldBeEmpty();
        theDaemon.IsIdentityParked(theShard.Name, null, id).ShouldBeFalse();

        // Both events folded
        await using var query = theStore.QuerySession();
        (await query.LoadAsync<ParkTally>(id, Cancellation)).ShouldNotBeNull().Sequences.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_discard_runs_when_the_shard_restarts_with_no_new_events()
    {
        var id = await requestWhileTheShardIsStoppedAsync(ParkedAction.Discard);

        await restartAndWaitForTheUnparkAsync();

        (await rowsAsync()).ShouldBeEmpty();
        theDaemon.IsIdentityParked(theShard.Name, null, id).ShouldBeFalse();

        // The poison event was dropped, the event before it kept
        await using var query = theStore.QuerySession();
        (await query.LoadAsync<ParkTally>(id, Cancellation)).ShouldNotBeNull().Sequences.Count.ShouldBe(1);
    }
}
