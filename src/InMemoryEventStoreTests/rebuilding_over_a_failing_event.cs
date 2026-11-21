using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace InMemoryEventStoreTests;

/// <summary>
/// jasperfx#1277: a rebuild that ends with its shard paused throws the shard's failure, and one that gets past its
/// failures under the rebuild's error options counts as done. A full rebuild never parks: parking only runs for a
/// continuous batch, so <c>RebuildErrors</c> set to Park pauses the rebuild like Pause does.
/// </summary>
public class rebuilding_over_a_failing_event : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly List<Guid> theStreams = new();
    private InMemoryDocumentStore theStore = null!;
    private IProjectionDaemon? theDaemon;

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

    private async Task<Guid[]> aStoreWithAPoisonedStreamAsync(Action<InMemoryProjectionGraph>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddInMemoryStoreForPrototyping(options =>
        {
            options.ConfigureEvents(e => e.AddEventType(typeof(Flaky)));
            options.ConfigureProjections(g =>
            {
                g.Snapshot<FlakyTally>(SnapshotLifecycle.Async);
                configure?.Invoke(g);
            });
        });

        theStore = services.BuildServiceProvider().GetRequiredService<InMemoryDocumentStore>();

        var streams = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        theStreams.AddRange(streams);

        await using var session = theStore.LightweightSession();
        foreach (var stream in streams) session.Events.StartStream<FlakyTally>(stream, new Flaky());
        await session.SaveChangesAsync(Cancellation);

        // Poisoned for the rebuild only
        FlakyFaults.Fail(streams[0], int.MaxValue, () => new InvalidOperationException("a bug in the projection"));

        theDaemon = await theStore.BuildProjectionDaemonAsync();
        return streams;
    }

    private Task rebuildAsync() => theDaemon!.RebuildProjectionAsync<FlakyTally>(Timeout, Cancellation);

    [Fact]
    public async Task a_rebuild_that_pauses_on_a_failing_event_throws_the_failure()
    {
        await aStoreWithAPoisonedStreamAsync();

        var thrown = await Should.ThrowAsync<ApplyEventException>(rebuildAsync());
        thrown.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("a bug in the projection");
    }

    [Fact]
    public async Task rebuild_errors_set_to_park_still_pause_and_throw_because_a_rebuild_never_parks()
    {
        await aStoreWithAPoisonedStreamAsync(g => g.RebuildErrors.ApplyErrors = ProjectionErrorAction.Park);

        await Should.ThrowAsync<ApplyEventException>(rebuildAsync());

        var database = (InMemoryEventDatabase)(await theStore.AllDatabases()).Single();
        (await database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation)).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_rebuild_that_skips_a_failing_event_completes()
    {
        var streams = await aStoreWithAPoisonedStreamAsync(g => g.RebuildErrors.ApplyErrors = ProjectionErrorAction.Skip);

        await rebuildAsync();

        theStore.DeadLetters(theStore.AllShards().Single().Name, null).Count.ShouldBe(1);

        await using var query = theStore.QuerySession();
        (await query.LoadAsync<FlakyTally>(streams[1], Cancellation)).ShouldNotBeNull().Applied.ShouldBe(1);
        (await query.LoadAsync<FlakyTally>(streams[2], Cancellation)).ShouldNotBeNull().Applied.ShouldBe(1);
    }
}
