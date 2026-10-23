using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#809: rebuildAgent hard-coded a floor of 0, so a rebuild ignored SubscribeFromSequence / FromTime (and
// any stored floor). A non-zero rebuild floor also has to seed the progression row that teardown just deleted:
// the first progress write is an INSERT only for a page starting at 0.
public class RebuildFromFloorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task a_rebuild_replays_from_a_configured_sequence_floor_and_seeds_the_row_first()
    {
        await using var harness = new Harness(options => options.SubscribeFromSequence(30));

        await harness.Daemon.RebuildProjectionAsync("Trips", tenantId: null, TestTimeout, TestContext.Current.CancellationToken);

        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            harness.Database, "Trips:All", Arg.Any<CancellationToken>(), 30L);
        harness.Loader.Floors.ShouldContain(30L);
        harness.Loader.Floors.ShouldNotContain(0L);
    }

    [Fact]
    public async Task a_rebuild_replays_from_a_stored_floor()
    {
        await using var harness = new Harness(_ => { });
        harness.Database.FetchProjectionFloorAsync("Trips", null, Arg.Any<CancellationToken>()).Returns(12L);

        await harness.Daemon.RebuildProjectionAsync("Trips", tenantId: null, TestTimeout, TestContext.Current.CancellationToken);

        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            harness.Database, "Trips:All", Arg.Any<CancellationToken>(), 12L);
        harness.Loader.Floors.ShouldContain(12L);
    }

    [Fact]
    public async Task a_default_rebuild_still_replays_from_zero_without_seeding()
    {
        await using var harness = new Harness(_ => { });

        await harness.Daemon.RebuildProjectionAsync("Trips", tenantId: null, TestTimeout, TestContext.Current.CancellationToken);

        await harness.Store.DidNotReceive().RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<long?>());
        harness.Loader.Floors.ShouldContain(0L);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Action<AsyncOptions> configure)
        {
            Loader = new FloorRecordingLoader();

            Store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            Store.Meter.Returns(new Meter("tests"));
            Store.TimeProvider.Returns(TimeProvider.System);
            Store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            Store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            Store.RebuildErrors.Returns(new ErrorHandlingOptions());
            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(Loader);
            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(Loader);

            Database = Substitute.For<IEventDatabase>();
            Database.Identifier.Returns("db1");
            Database.DatabaseUri.Returns(new Uri("fake://db1"));
            Database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));
            Database.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns((long?)null);

            var projections = new CrossProductRebuildCapTests.FakeProjectionGraph { MaxConcurrentEventLoadsPerDatabase = 0 };

            var options = new AsyncOptions();
            configure(options);

            var shard = new AsyncShard<FakeOperations, FakeSession>(options, ShardRole.Projection,
                new ShardName("Trips"), new CrossProductRebuildCapTests.CompletingSubscriptionFactory(), new EventFilterable());
            Store.AllShards().Returns([shard]);

            var source = Substitute.For<IProjectionSource<FakeOperations, FakeSession>>();
            source.Name.Returns("Trips");
            source.Shards().Returns([shard]);
            projections.All.Add(source);

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                Store, Database, new NulloLogger(), new FixedHighWaterDetector(100), projections);
        }

        public IEventStore<FakeOperations, FakeSession> Store { get; }
        public IEventDatabase Database { get; }
        public FloorRecordingLoader Loader { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    private sealed class FixedHighWaterDetector(long mark) : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics { CurrentMark = mark, HighestSequence = mark });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    // Records the floor of every page request, then answers with an already-caught-up page
    internal sealed class FloorRecordingLoader : IEventLoader
    {
        public ConcurrentBag<long> Floors { get; } = new();

        public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            Floors.Add(request.Floor);
            var page = new EventPage(request.Floor);
            page.CalculateCeiling(request.BatchSize, request.HighWater);
            return Task.FromResult(page);
        }
    }
}
