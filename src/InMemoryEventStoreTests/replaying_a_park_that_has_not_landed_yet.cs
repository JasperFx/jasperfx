using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using Shouldly;

namespace InMemoryEventStoreTests;

/// <summary>
/// jasperfx#1258: a batch that parks an identity commits its parked row, runs the store's after-commit hooks, and only
/// then hands the park to the shard. A replay or discard asked for in between used to answer NotParked for an identity
/// whose parked row was already committed. The store's after-commit hook holds the daemon in that window here.
/// </summary>
public class replaying_a_park_that_has_not_landed_yet : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly InMemoryDocumentStore theStore = new();
    private readonly List<Guid> thePoisoned = new();
    private readonly TaskCompletionSource theRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InMemoryEventDatabase theDatabase = null!;
    private IProjectionDaemon theDaemon = null!;
    private string theProjectionName = null!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theStore.Events.AddEventType(typeof(Counted));
        theStore.Events.AddEventType(typeof(CountedPoison));
        theStore.Projections.Snapshot<ParkTally>(SnapshotLifecycle.Async);
        theStore.ContinuousErrors.ApplyErrors = ProjectionErrorAction.Park;
        theProjectionName = theStore.AllShards().Single().Name.Name;

        theDatabase = (InMemoryEventDatabase)(await theStore.AllDatabases()).Single();

        theDaemon = await theStore.BuildProjectionDaemonAsync();
        await theDaemon.StartAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Never leave the daemon held, whatever the test did
        theStore.ProjectionBatchCommitted = null;
        theRelease.TrySetResult();

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

    // Parks a new identity and holds the daemon after the park's commit, before the shard takes the park
    private async Task<Guid> parkAndHoldInTheWindowAsync()
    {
        var id = await appendAsync(null, new Counted());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theStore.ProjectionBatchCommitted = async parks =>
        {
            if (!parks.Any(x => x.Identity.Equals(id))) return;

            theStore.ProjectionBatchCommitted = null;
            committed.TrySetResult();
            await theRelease.Task;
        };

        thePoisoned.Add(id);
        ParkTally.Poisoned[id] = true;
        await appendAsync(id, new CountedPoison());

        await committed.Task.WaitAsync(Timeout, Cancellation);

        // The window: the parked row is committed, and the shard does not hold the identity yet
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation))
            .ShouldHaveSingleItem().Identity.ShouldBe(id);
        theDaemon.IsIdentityParked(theProjectionName, null, id).ShouldBeFalse();

        return id;
    }

    [Fact]
    public async Task a_replay_waits_for_the_park_to_land_instead_of_answering_not_parked()
    {
        var id = await parkAndHoldInTheWindowAsync();

        ParkTally.Poisoned.TryRemove(id, out _);
        var replay = theDaemon.ReplayParkedIdentityAsync(theProjectionName, null, id, Cancellation);

        // Nothing can land the park while the daemon is held, so the replay is still waiting for it
        replay.IsCompleted.ShouldBeFalse();

        theRelease.SetResult();

        (await replay.WaitAsync(Timeout, Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Replayed);
        theDaemon.IsIdentityParked(theProjectionName, null, id).ShouldBeFalse();
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation)).ShouldBeEmpty();

        await using var query = theStore.QuerySession();
        (await query.LoadAsync<ParkTally>(id, Cancellation)).ShouldNotBeNull().Sequences.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_discard_waits_for_the_park_to_land_instead_of_answering_not_parked()
    {
        var id = await parkAndHoldInTheWindowAsync();

        var discard = theDaemon.DiscardParkedIdentityAsync(theProjectionName, null, id, Cancellation);
        discard.IsCompleted.ShouldBeFalse();

        theRelease.SetResult();

        (await discard.WaitAsync(Timeout, Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Discarded);
        theDaemon.IsIdentityParked(theProjectionName, null, id).ShouldBeFalse();
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation)).ShouldBeEmpty();

        // The poison event was dropped, the event before it kept
        await using var query = theStore.QuerySession();
        (await query.LoadAsync<ParkTally>(id, Cancellation)).ShouldNotBeNull().Sequences.Count.ShouldBe(1);
    }

    [Fact]
    public async Task an_identity_that_is_not_being_parked_still_answers_not_parked_at_once()
    {
        await parkAndHoldInTheWindowAsync();

        // Only the identities of the batch that has not landed are waited for
        var replay = theDaemon.ReplayParkedIdentityAsync(theProjectionName, null, Guid.NewGuid(), Cancellation);
        replay.IsCompleted.ShouldBeTrue();
        (await replay).Outcome.ShouldBe(ParkedReplayOutcome.NotParked);
    }
}
