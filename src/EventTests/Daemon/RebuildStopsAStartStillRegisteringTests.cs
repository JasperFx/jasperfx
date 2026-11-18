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
using static EventTests.Daemon.CrossProductRebuildCapTests;

namespace EventTests.Daemon;

// jasperfx#1233: a rebuild read the projection's running agents BEFORE it waited for the agent registry lock. A
// continuous start that had already passed its IsRebuilding check holds that lock until it registers its agent, so
// the rebuild missed the agent, tore the projection down under it, and the live agent committed a page afterwards
// (23505 on the progression row in Marten). The start is held between the check and registration by holding the
// progression read the start position needs, so the overlap is a held gate rather than a timing guess.
public class RebuildStopsAStartStillRegisteringTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task a_store_wide_rebuild_stops_an_agent_whose_start_was_registering_when_the_rebuild_began()
    {
        await using var harness = new StoreWideHarness();
        var start = new HeldStart(harness.Database);
        var agentsAtTeardown = harness.RecordAgentsAtTeardown();

        var started = harness.Daemon.StartAgentAsync(new ShardName("ProjA"), CancellationToken.None);
        await start.HeldAsync();

        var rebuild = harness.Daemon.RebuildProjectionAsync("ProjA", TestTimeout, CancellationToken.None);
        harness.Daemon.IsRebuilding(new ShardName("ProjA")).ShouldBeTrue();
        rebuild.IsCompleted.ShouldBeFalse();

        start.Release();
        await startEndsAsync(started);
        await rebuild.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        // The agent the start registered was stopped before the teardown, not after it
        agentsAtTeardown.ShouldBe(["ProjA:All=Stopped"]);
    }

    [Fact]
    public async Task a_tenant_rebuild_stops_an_agent_whose_start_was_registering_when_the_rebuild_began()
    {
        await using var harness = new RebuildHarness(["t1"], ["ProjA"], gated: false);
        var start = new HeldStart(harness.Database);
        var shard = new ShardName("ProjA").ForTenant("t1");

        var agentsAtTeardown = new List<string>();
        harness.Store.DeleteProjectionProgressAsync(harness.Database, "ProjA", "t1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                agentsAtTeardown.Add(agentsOf(harness.Daemon));
                return Task.CompletedTask;
            });

        var started = harness.Daemon.StartAgentAsync(shard, CancellationToken.None);
        await start.HeldAsync();

        var rebuild = harness.Daemon.RebuildProjectionAsync("ProjA", "t1", TestTimeout, CancellationToken.None);
        harness.Daemon.IsRebuilding(shard).ShouldBeTrue();
        rebuild.IsCompleted.ShouldBeFalse();

        start.Release();
        await startEndsAsync(started);
        await rebuild.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        agentsAtTeardown.ShouldBe(["ProjA:All:t1=Stopped"]);
    }

    // The start registered its agent before the rebuild stopped it, so it answers either that agent or, when it looks
    // again after the stop, that the projection is being rebuilt. Neither leaves the agent running.
    private static async Task startEndsAsync(Task<ISubscriptionAgent> started)
    {
        try
        {
            await started.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
        catch (ShardStartException e)
        {
            e.Reason.ShouldBe(ShardStartFailureReason.Rebuilding);
        }
    }

    private static string agentsOf(
        JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> daemon)
    {
        return string.Join(", ", daemon.CurrentAgents()
            .Where(x => x.Name.Name == "ProjA")
            .Select(x => $"{x.Name.Identity}={x.Status}"));
    }

    // The continuous start reads the shard's progression to find where it resumes, after the IsRebuilding check and
    // while it holds the registry lock. The first read is held until the test lets it go; later reads answer at once.
    private sealed class HeldStart
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public HeldStart(IEventDatabase database)
        {
            database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .Returns(_ => readAsync());
        }

        private async Task<long> readAsync()
        {
            if (Interlocked.Increment(ref _reads) > 1) return 0;

            _held.TrySetResult();
            await _release.Task;
            return 0;
        }

        public Task HeldAsync() => _held.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        public void Release() => _release.TrySetResult();
    }

    // Not partitioned per tenant, so a rebuild goes through the store-wide stop and teardown
    private sealed class StoreWideHarness : IAsyncDisposable
    {
        public StoreWideHarness()
        {
            Store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            Store.Meter.Returns(new Meter("tests"));
            Store.TimeProvider.Returns(TimeProvider.System);
            Store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            Store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            Store.RebuildErrors.Returns(new ErrorHandlingOptions());

            var loader = new GatedInstrumentedLoader(gated: false);
            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(loader);
            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

            Database = Substitute.For<IEventDatabase>();
            Database.Identifier.Returns("db1");
            Database.DatabaseUri.Returns(new Uri("fake://db1"));
            Database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

            var projections = new FakeProjectionGraph { MaxConcurrentEventLoadsPerDatabase = 0 };

            var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
                new ShardName("ProjA"), new CompletingSubscriptionFactory(), new EventFilterable());
            Store.AllShards().Returns([shard]);

            var source = Substitute.For<IProjectionSource<FakeOperations, FakeSession>>();
            source.Name.Returns("ProjA");
            source.Shards().Returns([shard]);
            projections.All.Add(source);

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                Store, Database, new NulloLogger(), new Detector(), projections);
        }

        public IEventStore<FakeOperations, FakeSession> Store { get; }
        public IEventDatabase Database { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public List<string> RecordAgentsAtTeardown()
        {
            var agents = new List<string>();
            Store.TeardownExistingProjectionStateAsync(Database, "ProjA", Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    agents.Add(agentsOf(Daemon));
                    return Task.CompletedTask;
                });

            return agents;
        }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    private sealed class Detector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");
        public bool SupportsTenantPartitioning => false;

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics { CurrentMark = 10, HighestSequence = 10, LastMark = 0 });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => throw new NotSupportedException();

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => throw new NotSupportedException();
    }
}
