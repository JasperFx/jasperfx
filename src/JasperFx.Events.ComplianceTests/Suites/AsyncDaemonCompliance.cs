using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

public record ItemAdded(string Name);

public record ItemRemoved(string Name);

/// <summary>
/// A deliberately plain self-aggregating snapshot type for the async daemon suite.
/// </summary>
public partial class DaemonItemTally
{
    public Guid Id { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }

    public void Apply(ItemAdded e) => AddedCount++;

    public void Apply(ItemRemoved e) => RemovedCount++;
}

/// <summary>
/// Proves the fixture's daemon plumbing end to end: an async snapshot projection registered through
/// <see cref="ComplianceStoreConfig"/>, driven by a started <see cref="Daemon.IProjectionDaemon"/>,
/// waited on through the store's non-stale hook, asserted against the *persisted* document, and
/// rebuilt once from the event stream.
/// </summary>
/// <remarks>
/// Deliberately small and single-tenant. Multi-node, HotCold and distribution behavior is
/// product-specific and stays out of compliance scope.
/// </remarks>
public abstract class AsyncDaemonCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_daemon";

        config.AddEventType<ItemAdded>();
        config.AddEventType<ItemRemoved>();

        config.Snapshot<DaemonItemTally>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Capability gate. xunit v3's declarative SkipUnless needs a *static* property, which cannot
    /// consult a per-store fixture instance, so the gate runs as a dynamic skip instead.
    /// </summary>
    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");
    }

    [Fact]
    public async Task async_projection_catches_up_and_persists_the_document()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<DaemonItemTally>(streamId, new ItemAdded("one"), new ItemAdded("two"),
                new ItemRemoved("one"));
            await SaveChangesAsync(session);
        }

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var query = OpenSession();
        var tally = await LoadDocumentAsync<DaemonItemTally>(query, streamId);
        tally.ShouldNotBeNull();
        tally.AddedCount.ShouldBe(2);
        tally.RemovedCount.ShouldBe(1);
    }

    [Fact]
    public async Task rebuild_the_projection_from_the_event_stream()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<DaemonItemTally>(streamId, new ItemAdded("one"), new ItemAdded("two"));
            await SaveChangesAsync(session);
        }

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.RebuildProjectionAsync<DaemonItemTally>(Cancellation);

        await using var query = OpenSession();
        var tally = await LoadDocumentAsync<DaemonItemTally>(query, streamId);
        tally.ShouldNotBeNull();
        tally.AddedCount.ShouldBe(2);
        tally.RemovedCount.ShouldBe(0);
    }

    private void SkipUnlessProgressionLastUpdatedIsSupported()
    {
        SkipUnlessDaemonIsSupported();
        Assert.SkipUnless(theFixture.SupportsProgressionLastUpdated,
            "This event store does not populate ShardState.LastUpdated from AllProjectionProgress");
    }

    private async Task<IEventDatabase> theOnlyDatabaseAsync()
    {
        var databases = await EventStore.AllDatabases();
        return databases.ShouldHaveSingleItem();
    }

    /// <summary>
    /// Every row <c>AllProjectionProgress</c> returns carries the progression row's own
    /// <c>last_updated</c>, read as a UTC instant (jasperfx#924).
    /// </summary>
    /// <remarks>
    /// The window check is wide on purpose — the database clock and the test host's clock are not the
    /// same clock — but narrow enough to catch a store that reads a zone-less column as local time
    /// and shifts it by hours, which would make a live row look dead (or a dead one live).
    /// </remarks>
    [Fact]
    public async Task all_projection_progress_carries_each_rows_last_updated()
    {
        SkipUnlessProgressionLastUpdatedIsSupported();

        var floor = DateTimeOffset.UtcNow.AddMinutes(-5);

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<DaemonItemTally>(Guid.NewGuid(), new ItemAdded("one"));
            await SaveChangesAsync(session);
        }

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var database = await theOnlyDatabaseAsync();
        var progress = await database.AllProjectionProgress(Cancellation);

        var ceiling = DateTimeOffset.UtcNow.AddMinutes(5);

        progress.ShouldContain(x => x.ShardName == ShardState.HighWaterMark);
        progress.ShouldContain(x => x.ShardName != ShardState.HighWaterMark);

        foreach (var state in progress)
        {
            state.LastUpdated.ShouldNotBeNull($"{state.ShardName} came back without LastUpdated");
            state.LastUpdated.Value.ShouldBeGreaterThan(floor, $"{state.ShardName}.LastUpdated");
            state.LastUpdated.Value.ShouldBeLessThan(ceiling, $"{state.ShardName}.LastUpdated");
        }
    }

    /// <summary>
    /// The property that makes <see cref="ShardState.LastUpdated" /> a liveness signal at all: the
    /// high-water row's <c>last_updated</c> keeps moving while the daemon runs with <em>no new
    /// events</em>, when neither the sequence nor <see cref="ShardState.LastAdvanced" /> does
    /// (jasperfx#924, CritterWatch#1359).
    /// </summary>
    /// <remarks>
    /// Without this, a store could satisfy the fact above by stamping the read time, and a monitor
    /// could not tell "caught up, no new events" from "no longer maintained" — which is the whole ask.
    /// It polls rather than sleeping a fixed interval, because each store's high-water detector sets
    /// its own idle cadence.
    /// </remarks>
    [Fact]
    public async Task the_high_water_rows_last_updated_moves_on_an_idle_daemon()
    {
        SkipUnlessProgressionLastUpdatedIsSupported();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<DaemonItemTally>(Guid.NewGuid(), new ItemAdded("one"));
            await SaveChangesAsync(session);
        }

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var database = await theOnlyDatabaseAsync();

        async Task<ShardState> highWaterAsync()
        {
            var progress = await database.AllProjectionProgress(Cancellation);
            return progress.Single(x => x.ShardName == ShardState.HighWaterMark);
        }

        var first = await highWaterAsync();
        first.LastUpdated.ShouldNotBeNull();

        var deadline = DateTimeOffset.UtcNow.Add(_timeout);
        var latest = first;
        while (latest.LastUpdated <= first.LastUpdated && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, Cancellation);
            latest = await highWaterAsync();
        }

        // Nothing was appended, so the mark itself must not have moved -- only its freshness.
        latest.Sequence.ShouldBe(first.Sequence);
        latest.LastUpdated.ShouldNotBeNull();
        latest.LastUpdated.Value.ShouldBeGreaterThan(first.LastUpdated.Value,
            "The HighWaterMark row's last_updated did not move on an idle daemon, so it cannot distinguish a caught-up mark from an abandoned one");
    }

    [Fact]
    public void registered_shard_names_are_reachable_from_the_non_generic_store()
    {
        // jasperfx#815. Asserted off IEventStore rather than the closed generic on purpose: a consumer
        // that ships ONE assembly against all three stores (CritterWatch's Wolverine.CritterWatch) can
        // only hold the non-generic interface, and AllShards() is not on it. This is the "expected" side
        // of the expected-versus-observed correlation that FetchProjectionLagAsync runs against a single
        // database's progression rows — without it, a shard registered but never started on some
        // databases of a multi-database store has nothing to be missing FROM.
        SkipUnlessDaemonIsSupported();

        EventStore.RegisteredShardNames()
            .ShouldContain(x => x.Name == nameof(DaemonItemTally));
    }
}
