using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace InMemoryEventStoreTests;

/// <summary>
/// jasperfx#1267: the in-memory store's daemon reads its error handling from <c>Projections.Errors</c> and
/// <c>Projections.RebuildErrors</c>, like every other store, and the store's own continuous default is still Pause.
/// </summary>
public class in_memory_store_projection_errors : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly List<Guid> theStreams = new();
    private InMemoryDocumentStore theStore = null!;
    private InMemoryEventDatabase theDatabase = null!;
    private IProjectionDaemon? theDaemon;
    private Task<ShardState> thePause = null!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in theStreams) FlakyFaults.Forget(stream);

        if (theDaemon != null)
        {
            await theDaemon.StopAllAsync();
            theDaemon.Dispose();
        }
    }

    // Built the way an application builds it, through AddInMemoryStoreForPrototyping
    private void buildStore(Action<InMemoryStoreOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddInMemoryStoreForPrototyping(options =>
        {
            options.ConfigureEvents(e => e.AddEventType(typeof(Flaky)));
            options.ConfigureProjections(g => g.Snapshot<FlakyTally>(SnapshotLifecycle.Async));
            configure?.Invoke(options);
        });

        theStore = services.BuildServiceProvider().GetRequiredService<InMemoryDocumentStore>();
    }

    // Three streams, the first of which fails every time it is applied
    private async Task<Guid[]> appendWithAPoisonedStreamAsync()
    {
        var streams = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        theStreams.AddRange(streams);
        FlakyFaults.Fail(streams[0], int.MaxValue, () => new InvalidOperationException("a bug in the projection"));

        await using var session = theStore.LightweightSession();
        foreach (var stream in streams) session.Events.StartStream<FlakyTally>(stream, new Flaky());
        await session.SaveChangesAsync(Cancellation);

        return streams;
    }

    private async Task startAsync()
    {
        theDatabase = (InMemoryEventDatabase)(await theStore.AllDatabases()).Single();

        // Watching before anything runs, so a pause can't be missed
        thePause = theDatabase.Tracker.WaitForShardCondition(x => x.Action == ShardAction.Paused, "a paused shard",
            Timeout);

        theDaemon = await theStore.BuildProjectionDaemonAsync();
        await theDaemon.StartAllAsync();
    }

    private int deadLetters() => theStore.DeadLetters(theStore.AllShards().Single().Name, null).Count;

    [Fact]
    public void the_continuous_default_is_pause()
    {
        buildStore();

        theStore.Projections.Errors.ApplyErrors.ShouldBe(ProjectionErrorAction.Pause);
        theStore.Projections.Errors.SerializationErrors.ShouldBe(ProjectionErrorAction.Pause);
        theStore.Projections.Errors.SkipUnknownEvents.ShouldBeFalse();

        theStore.Projections.RebuildErrors.ApplyErrors.ShouldBe(ProjectionErrorAction.Pause);
        theStore.Projections.RebuildErrors.SerializationErrors.ShouldBe(ProjectionErrorAction.Pause);
    }

    [Fact]
    public void the_daemon_reads_the_projection_graphs_options()
    {
        buildStore();
        var store = (IEventStore<IInMemoryDocumentSession, IInMemoryQuerySession>)theStore;

        store.ContinuousErrors.ShouldBeSameAs(theStore.Projections.Errors);
        store.RebuildErrors.ShouldBeSameAs(theStore.Projections.RebuildErrors);
        store.ErrorHandlingOptions(ShardExecutionMode.Continuous).ShouldBeSameAs(theStore.Projections.Errors);
        store.ErrorHandlingOptions(ShardExecutionMode.Rebuild).ShouldBeSameAs(theStore.Projections.RebuildErrors);
    }

    [Fact]
    public async Task by_default_a_failing_event_pauses_the_shard()
    {
        buildStore();
        var streams = await appendWithAPoisonedStreamAsync();

        await startAsync();

        var paused = await thePause;
        paused.Failure.ShouldNotBeNull().Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        FlakyFaults.Attempts(streams[0]).ShouldBe(1);
        deadLetters().ShouldBe(0);
    }

    [Fact]
    public async Task skip_set_through_configure_projections_takes_effect()
    {
        buildStore(options => options.ConfigureProjections(g => g.Errors.ApplyErrors = ProjectionErrorAction.Skip));
        var streams = await appendWithAPoisonedStreamAsync();

        await startAsync();
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        deadLetters().ShouldBe(1);
        thePause.IsCompleted.ShouldBeFalse();

        await using var query = theStore.QuerySession();
        (await query.LoadAsync<FlakyTally>(streams[1], Cancellation)).ShouldNotBeNull().Applied.ShouldBe(1);
        (await query.LoadAsync<FlakyTally>(streams[2], Cancellation)).ShouldNotBeNull().Applied.ShouldBe(1);
    }

    [Fact]
    public async Task park_set_through_configure_projections_takes_effect()
    {
        buildStore(options => options.ConfigureProjections(g => g.Errors.ApplyErrors = ProjectionErrorAction.Park));
        var streams = await appendWithAPoisonedStreamAsync();

        await startAsync();
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation))
            .ShouldHaveSingleItem().Identity.ShouldBe(streams[0]);
        thePause.IsCompleted.ShouldBeFalse();
    }

#pragma warning disable CS0618 // the obsolete aliases are what these cover
    [Fact]
    public void the_obsolete_aliases_are_the_projection_graphs_options()
    {
        buildStore();

        theStore.ContinuousErrors.ShouldBeSameAs(theStore.Projections.Errors);
        theStore.RebuildErrors.ShouldBeSameAs(theStore.Projections.RebuildErrors);
    }

    [Fact]
    public async Task setting_the_obsolete_continuous_alias_still_takes_effect()
    {
        buildStore();
        theStore.ContinuousErrors.ApplyErrors = ProjectionErrorAction.Skip;

        theStore.Projections.Errors.ApplyErrors.ShouldBe(ProjectionErrorAction.Skip);

        await appendWithAPoisonedStreamAsync();
        await startAsync();
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        deadLetters().ShouldBe(1);
        thePause.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void setting_the_obsolete_rebuild_alias_sets_the_graphs_rebuild_options()
    {
        buildStore();
        theStore.RebuildErrors.SkipApplyErrors = true;

        theStore.Projections.RebuildErrors.ApplyErrors.ShouldBe(ProjectionErrorAction.Skip);
    }
#pragma warning restore CS0618
}
