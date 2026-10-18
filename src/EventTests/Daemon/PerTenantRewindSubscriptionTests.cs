using System.Diagnostics.Metrics;
using EventTests.Projections;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#407 declared IProjectionDaemon.RewindSubscriptionAsync(name, tenantId, ...) with a default that throws
// for a tenant, and the tenant-less rewind was never taught about per-tenant event partitioning. Under it the
// daemon runs one agent per tenant ("Sub:All:t1"), every tenant draws its own sequence, and the store keeps one
// progression row per tenant. The tenant-less rewind stopped every tenant's agent, rewound the store-global row
// only, and restarted a store-global agent that reads every tenant's overlapping sequences - the fan-out that
// StartAllAsync (marten#4717) and the store-global rebuild (CritterWatch#303) already do was missing here.
public class PerTenantRewindSubscriptionTests
{
    private static readonly DateTimeOffset Yesterday = DateTimeOffset.UtcNow.AddDays(-1);

    [Fact]
    public async Task a_per_tenant_rewind_restarts_only_that_tenants_agent_from_the_floor()
    {
        await using var harness = new PartitionedHarness(["t1", "t2"]);
        IProjectionDaemon daemon = harness.Daemon;
        await daemon.StartAllAsync();
        var untouched = agentFor(daemon, "Sub:All:t2");

        await daemon.RewindSubscriptionAsync("Sub", "t1", TestContext.Current.CancellationToken, 5);

        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub:All:t1", Arg.Any<CancellationToken>(), 5L);
        await harness.Store.DidNotReceive().RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), Arg.Is<string>(x => x != "Sub:All:t1"), Arg.Any<CancellationToken>(),
            Arg.Any<long?>());

        runningIdentities(daemon).ShouldBe(["Sub:All:t1", "Sub:All:t2"], ignoreOrder: true);
        ReferenceEquals(agentFor(daemon, "Sub:All:t2"), untouched).ShouldBeTrue();

        // Restarted from the floor against its own tenant's ceiling, not the store-global mark
        var rewound = await startedAsync(agentFor(daemon, "Sub:All:t1"));
        rewound.LastCommitted.ShouldBe(5);
        rewound.HighWaterMark.ShouldBe(42);
    }

    [Fact]
    public async Task a_store_global_rewind_to_zero_rewinds_every_tenant_and_keeps_per_tenant_agents()
    {
        await using var harness = new PartitionedHarness(["t1", "t2"]);
        IProjectionDaemon daemon = harness.Daemon;
        await daemon.StartAllAsync();

        await daemon.RewindSubscriptionAsync("Sub", TestContext.Current.CancellationToken, 0);

        // Per-tenant agents again - never a store-global "Sub:All" reading every tenant's sequences
        runningIdentities(daemon).ShouldBe(["Sub:All:t1", "Sub:All:t2"], ignoreOrder: true);

        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub:All:t1", Arg.Any<CancellationToken>(), 0L);
        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub:All:t2", Arg.Any<CancellationToken>(), 0L);
        await harness.Store.DidNotReceive().RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub", Arg.Any<CancellationToken>(), Arg.Any<long?>());
    }

    [Fact]
    public async Task a_store_global_rewind_rewinds_only_the_tenant_agents_this_daemon_runs()
    {
        // A node under Wolverine-managed distribution runs only the tenant agents assigned to it. Another node
        // runs t2: rewinding t2's row from here would fail that agent's next progress write, and starting t2
        // here would run it twice.
        await using var harness = new PartitionedHarness(["t1", "t2"]);
        IProjectionDaemon daemon = harness.Daemon;
        await daemon.StartAgentAsync("Sub:All:t1", TestContext.Current.CancellationToken);

        await daemon.RewindSubscriptionAsync("Sub", TestContext.Current.CancellationToken, 0);

        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub:All:t1", Arg.Any<CancellationToken>(), 0L);
        await harness.Store.DidNotReceive().RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), Arg.Is<string>(x => x != "Sub:All:t1"), Arg.Any<CancellationToken>(),
            Arg.Any<long?>());
        runningIdentities(daemon).ShouldBe(["Sub:All:t1"]);
    }

    [Fact]
    public async Task a_store_global_rewind_with_no_tenant_agent_running_keeps_the_store_global_path()
    {
        // No tenant registered yet: StartAllAsync keeps the store-global shard, and that is what gets rewound
        await using var harness = new PartitionedHarness([]);
        IProjectionDaemon daemon = harness.Daemon;
        await daemon.StartAllAsync();

        await daemon.RewindSubscriptionAsync("Sub", TestContext.Current.CancellationToken, 0);

        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub", Arg.Any<CancellationToken>(), 0L);
        runningIdentities(daemon).ShouldBe(["Sub:All"]);
    }

    [Fact]
    public async Task a_store_global_rewind_to_a_sequence_floor_is_refused_under_partitioning()
    {
        await using var harness = new PartitionedHarness(["t1", "t2"]);
        IProjectionDaemon daemon = harness.Daemon;
        await daemon.StartAllAsync();
        var before = daemon.CurrentAgents().ToArray();

        // A sequence number belongs to one tenant's own numbering: applying it to every tenant would skip
        // events in a tenant that is behind it and replay the wrong ones in a tenant that is ahead of it
        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            daemon.RewindSubscriptionAsync("Sub", TestContext.Current.CancellationToken, 5));
        ex.Message.ShouldContain("RewindSubscriptionAsync(subscriptionName, tenantId");

        await harness.Store.DidNotReceive().RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<long?>());
        daemon.CurrentAgents().ShouldBe(before);
        runningIdentities(daemon).ShouldBe(["Sub:All:t1", "Sub:All:t2"], ignoreOrder: true);
    }

    [Fact]
    public async Task a_store_global_rewind_by_time_resolves_each_tenants_own_floor()
    {
        await using var harness = new PartitionedHarness(["t1", "t2"]);
        IProjectionDaemon daemon = harness.Daemon;

        // The store-global floor is a position in whichever tenant's sequence happens to come first
        harness.Database.FindEventStoreFloorAtTimeAsync(Yesterday, Arg.Any<CancellationToken>())
            .Returns(3L);
        harness.Database.FindEventStoreFloorAtTimeAsync(Yesterday, "t1", Arg.Any<CancellationToken>())
            .Returns(7L);
        harness.Database.FindEventStoreFloorAtTimeAsync(Yesterday, "t2", Arg.Any<CancellationToken>())
            .Returns((long?)null);

        await daemon.StartAllAsync();
        var untouched = agentFor(daemon, "Sub:All:t2");

        await daemon.RewindSubscriptionAsync("Sub", TestContext.Current.CancellationToken, timestamp: Yesterday);

        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub:All:t1", Arg.Any<CancellationToken>(), 7L);

        // t2 has nothing at or after that time, so there is nothing to replay and it keeps running
        await harness.Store.DidNotReceive().RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), Arg.Is<string>(x => x != "Sub:All:t1"), Arg.Any<CancellationToken>(),
            Arg.Any<long?>());
        ReferenceEquals(agentFor(daemon, "Sub:All:t2"), untouched).ShouldBeTrue();
        runningIdentities(daemon).ShouldBe(["Sub:All:t1", "Sub:All:t2"], ignoreOrder: true);
    }

    [Fact]
    public async Task a_per_tenant_rewind_is_still_not_supported_without_tenant_partitioning()
    {
        await using var harness = new PartitionedHarness([], new GlobalOnlyStubDetector());
        IProjectionDaemon daemon = harness.Daemon;

        await Should.ThrowAsync<NotSupportedException>(() =>
            daemon.RewindSubscriptionAsync("Sub", "t1", TestContext.Current.CancellationToken, 5));
    }

    private static string[] runningIdentities(IProjectionDaemon daemon) => daemon.CurrentAgents()
        .Where(x => x.Status == AgentStatus.Running)
        .Select(x => x.Name.Identity)
        .ToArray();

    private static ISubscriptionAgent agentFor(IProjectionDaemon daemon, string identity) =>
        daemon.CurrentAgents().Single(x => x.Name.Identity == identity);

    // The Start command is processed by the agent's command loop, after StartAsync returns
    private static async Task<SubscriptionAgent> startedAsync(ISubscriptionAgent candidate)
    {
        var agent = candidate.ShouldBeOfType<SubscriptionAgent>();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (agent.Failure == null && agent.HighWaterMark == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return agent;
    }

    // A real daemon and real SubscriptionAgents over a substituted store and database. The database doubles as
    // the cross-tenant source, as Marten's does under per-tenant partitioning; every tenant's ceiling is 42 and
    // the store-global mark stays at 0.
    private sealed class PartitionedHarness : IAsyncDisposable
    {
        public PartitionedHarness(string[] tenants, IHighWaterDetector? detector = null)
        {
            var partitioned = new StubPartitionedDetector();
            foreach (var tenantId in tenants)
            {
                partitioned.SetTenantMark(tenantId, 42);
            }

            Store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            Store.Meter.Returns(new Meter("tests"));
            Store.TimeProvider.Returns(TimeProvider.System);
            Store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            Store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            Store.RebuildErrors.Returns(new ErrorHandlingOptions());

            // An empty page that is already caught up keeps a started agent Running with no storage behind it
            var loader = Substitute.For<IEventLoader>();
            loader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var request = callInfo.Arg<EventRequest>();
                    var page = new EventPage(request.Floor);
                    page.CalculateCeiling(request.BatchSize, request.HighWater);
                    return Task.FromResult(page);
                });

            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(loader);
            Store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

            var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();
            Store.AllShards().Returns([
                new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Subscription,
                    new ShardName("Sub"), factory, new EventFilterable())
            ]);

            Database = Substitute.For<IEventDatabase, ICrossTenantRebuildSource>();
            Database.Identifier.Returns("db1");
            Database.DatabaseUri.Returns(new Uri("fake://db1"));
            Database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));
            ((ICrossTenantRebuildSource)Database)
                .FindRebuildTenantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<string>>(tenants));

            Daemon = new TestProjectionDaemon(Store, Database, detector ?? partitioned);
        }

        public IEventStore<FakeOperations, FakeSession> Store { get; }
        public IEventDatabase Database { get; }
        public TestProjectionDaemon Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    // The shape a store's daemon takes (Marten's ProjectionDaemon): the shared daemon plus IProjectionDaemon, whose
    // members the base class implements - a per-tenant overload the base class does not declare is left to the
    // interface's default
    private sealed class TestProjectionDaemon(
        IEventStore<FakeOperations, FakeSession> store,
        IEventDatabase database,
        IHighWaterDetector detector)
        : JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(store, database,
            new NulloLogger(), detector, new FakeProjectionGraph()), IProjectionDaemon;

    private sealed class FakeProjectionGraph :
        ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeProjectionGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }

    private sealed class StubPartitionedDetector : IHighWaterDetector
    {
        private readonly Dictionary<string, long> _marks = new();

        public Uri DatabaseUri { get; } = new("fake://db1");

        public bool SupportsTenantPartitioning => true;

        public void SetTenantMark(string tenantId, long mark) => _marks[tenantId] = mark;

        public Task<HighWaterStatistics> Detect(CancellationToken token) => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
        {
            var statistics = tenantIds.Select(tenantId =>
            {
                var mark = _marks.GetValueOrDefault(tenantId);
                return new HighWaterStatistics
                {
                    TenantId = tenantId, CurrentMark = mark, LastMark = mark, HighestSequence = mark
                };
            }).ToArray();

            return Task.FromResult(new HighWaterVector(statistics));
        }

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => DetectForTenantsAsync(tenantIds, token);
    }

    private sealed class GlobalOnlyStubDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token) => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }
}
