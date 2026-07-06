using System.Diagnostics.Metrics;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// wolverine#3280: under node-distributed daemons (Wolverine-managed event-subscription distribution
// against a store that partitions events per tenant), a single tenant-bearing shard identity like
// "Trip:All:t1" is requested individually via StartAgentAsync(string, ...). AllShards() only carries the
// store-global identities, so the daemon must resolve the BASE shard, prime that tenant's high-water
// ceiling in the TenantedHighWaterCoordinator BEFORE starting, and then fan out a tenant-scoped agent —
// otherwise a SubscribeFromPresent subscription resolves "present" against high-water 0 and rewinds its
// progression row to the beginning, replaying the tenant's entire history. These tests pin the behavior
// of that per-tenant branch in isolation with a substituted store/database and a stub detector.
public class PerTenantStartAgentAsyncTests
{
    private static AsyncShard<FakeOperations, FakeSession> shardFor(ShardName name, AsyncOptions? options = null)
    {
        // The factory's BuildExecution auto-returns a substituted ISubscriptionExecution whose
        // TryBuildReplayExecutor is false, so a started agent runs the plain continuous path.
        var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();

        return new AsyncShard<FakeOperations, FakeSession>(options ?? new AsyncOptions(), ShardRole.Projection,
            name, factory, new EventFilterable());
    }

    [Fact]
    public async Task starts_a_per_tenant_agent_for_a_tenant_bearing_identity()
    {
        var detector = new StubPartitionedDetector();
        detector.SetTenantMark("t1", 42);

        await using var harness = new DaemonHarness(detector, shardFor(new ShardName("Trip")));

        await harness.Daemon.StartAgentAsync("Trip:All:t1", CancellationToken.None);

        var agent = harness.Daemon.CurrentAgents().ShouldHaveSingleItem();
        agent.Name.Identity.ShouldBe("Trip:All:t1");
        agent.Name.TenantId.ShouldBe("t1");
        agent.Status.ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task primes_the_tenant_ceiling_before_determining_the_starting_position()
    {
        // THE wolverine#3280 / SubscribeFromPresent regression pin. The FromPresent strategy resolves
        // "present" to the high-water mark it is handed. The store-global mark in this harness is pinned
        // at 0, so the ONLY way the rewind floor can be 42 is if the tenant's ceiling was polled and
        // primed in the coordinator BEFORE tryStartAgentAsync determined the starting position. Starting
        // first and polling after would compute "present" as sequence 0 and rewind the progression row
        // to the beginning, replaying the tenant's entire history.
        var detector = new StubPartitionedDetector();
        detector.SetTenantMark("t1", 42);

        var shard = shardFor(new ShardName("Sub"), new AsyncOptions().SubscribeFromPresent());
        await using var harness = new DaemonHarness(detector, shard);

        await harness.Daemon.StartAgentAsync("Sub:All:t1", CancellationToken.None);

        // FromPresent says ShouldUpdateProgressFirst, so the progress row is rewound to the PRIMED
        // per-tenant ceiling of 42...
        await harness.Store.Received(1).RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub:All:t1", Arg.Any<CancellationToken>(), 42L);

        // ... and never to the un-primed store-global mark of 0.
        await harness.Store.DidNotReceive().RewindSubscriptionProgressAsync(
            Arg.Any<IEventDatabase>(), "Sub:All:t1", Arg.Any<CancellationToken>(), 0L);
    }

    [Fact]
    public async Task throws_for_a_tenant_identity_when_the_store_has_no_tenant_partitioning()
    {
        // Without per-tenant high-water tracking a tenant agent would seed from the store-global mark
        // and double-process events already covered by the store-global agent — fail loudly instead.
        await using var harness = new DaemonHarness(new GlobalOnlyStubDetector(), shardFor(new ShardName("Trip")));

        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => harness.Daemon.StartAgentAsync("Trip:All:t1", CancellationToken.None));

        ex.Message.ShouldContain("does not use per-tenant event partitioning");

        harness.Daemon.CurrentAgents().ShouldBeEmpty();
    }

    [Fact]
    public async Task throws_for_an_unknown_base_shard()
    {
        // Tenant-bearing identity on a partitioned store, but no registered shard matches the base
        // identity "Nope:All" — the error lists the valid registered options.
        await using var harness = new DaemonHarness(new StubPartitionedDetector(), shardFor(new ShardName("Trip")));

        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => harness.Daemon.StartAgentAsync("Nope:All:t1", CancellationToken.None));

        ex.Message.ShouldContain("Unknown shard name 'Nope:All:t1'");
        ex.Message.ShouldContain("Trip:All");

        harness.Daemon.CurrentAgents().ShouldBeEmpty();
    }

    [Fact]
    public async Task an_exactly_registered_identity_wins_over_tenant_parsing()
    {
        // A registered store-global shard whose identity happens to carry enough segments to parse as
        // tenant-bearing must be started as-is, never hijacked by the per-tenant branch.
        var registered = new ShardName("Fancy:Weird"); // Identity "Fancy:Weird:All"

        // Sanity check the trap actually exists: this identity genuinely parses as tenant-bearing,
        // with the trailing "All" segment landing in the tenant slot.
        ShardName.TryParse(registered.Identity, out var parsed).ShouldBeTrue();
        parsed!.TenantId.ShouldBe("All");

        var detector = new StubPartitionedDetector();
        await using var harness = new DaemonHarness(detector, shardFor(registered));

        await harness.Daemon.StartAgentAsync("Fancy:Weird:All", CancellationToken.None);

        var agent = harness.Daemon.CurrentAgents().ShouldHaveSingleItem();
        agent.Name.Identity.ShouldBe("Fancy:Weird:All");
        agent.Name.TenantId.ShouldBeNull();
        agent.Status.ShouldBe(AgentStatus.Running);

        // The pseudo-tenant "All" was never activated in the coordinator's polled set.
        detector.PolledTenants().ShouldNotContain("All");
    }

    [Fact]
    public async Task resolves_versioned_tenant_identities()
    {
        // Versioned grammar: base shard "Trip:V2:All" + tenant slot -> "Trip:V2:All:t1".
        var detector = new StubPartitionedDetector();
        detector.SetTenantMark("t1", 7);

        await using var harness =
            new DaemonHarness(detector, shardFor(ShardName.Compose("Trip", "All", null, 2)));

        await harness.Daemon.StartAgentAsync("Trip:V2:All:t1", CancellationToken.None);

        var agent = harness.Daemon.CurrentAgents().ShouldHaveSingleItem();
        agent.Name.Identity.ShouldBe("Trip:V2:All:t1");
        agent.Name.TenantId.ShouldBe("t1");
        agent.Name.Version.ShouldBe(2u);
        agent.Status.ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task second_start_of_the_same_tenant_identity_is_idempotent()
    {
        var detector = new StubPartitionedDetector();
        detector.SetTenantMark("t1", 42);

        await using var harness = new DaemonHarness(detector, shardFor(new ShardName("Trip")));

        await harness.Daemon.StartAgentAsync("Trip:All:t1", CancellationToken.None);
        await harness.Daemon.StartAgentAsync("Trip:All:t1", CancellationToken.None);

        // Still exactly one running agent for the identity; the duplicate was quietly disposed.
        var agent = harness.Daemon.CurrentAgents().ShouldHaveSingleItem();
        agent.Name.Identity.ShouldBe("Trip:All:t1");
        agent.Status.ShouldBe(AgentStatus.Running);
    }

    // Real daemon + real SubscriptionAgents over a substituted store/database. The store-global
    // high-water detector reading is pinned at mark 0, so any nonzero seed/floor a test observes can
    // only have come from the per-tenant coordinator.
    private sealed class DaemonHarness : IAsyncDisposable
    {
        public DaemonHarness(IHighWaterDetector detector, params AsyncShard<FakeOperations, FakeSession>[] shards)
        {
            Store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            Store.Meter.Returns(new Meter("tests"));
            Store.TimeProvider.Returns(TimeProvider.System);
            // Keep StartHighWaterDetectionAsync away from EnsureStorageExistsAsync
            Store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            Store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            Store.RebuildErrors.Returns(new ErrorHandlingOptions());
            Store.AllShards().Returns(shards);

            // The loader hands back an empty, already-caught-up page so a started agent stays healthy
            // (Status == Running) without any real event storage behind it.
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

            Database = Substitute.For<IEventDatabase>();
            Database.Identifier.Returns("db1");
            Database.DatabaseUri.Returns(new Uri("fake://db1"));
            Database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                Store, Database, new NulloLogger(), detector, new FakeProjectionGraph());
        }

        public IEventStore<FakeOperations, FakeSession> Store { get; }
        public IEventDatabase Database { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    // Minimal concrete ProjectionGraph — the daemon only consumes it as DaemonSettings on these code
    // paths (shard resolution goes through the substituted store's AllShards()).
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

    // A detector for a store WITH per-tenant event partitioning. The store-global Detect() stays pinned
    // at mark 0; per-tenant marks are whatever the test seeded via SetTenantMark.
    private sealed class StubPartitionedDetector : IHighWaterDetector
    {
        private readonly Dictionary<string, long> _marks = new();
        private readonly List<string[]> _tenantPolls = new();

        public Uri DatabaseUri { get; } = new("fake://db1");

        public bool SupportsTenantPartitioning => true;

        public void SetTenantMark(string tenantId, long mark)
        {
            _marks[tenantId] = mark;
        }

        // Every tenant id the vectorized poll was ever asked about
        public IReadOnlyList<string> PolledTenants()
        {
            lock (_tenantPolls)
            {
                return _tenantPolls.SelectMany(x => x).Distinct().ToList();
            }
        }

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
        {
            lock (_tenantPolls)
            {
                _tenantPolls.Add(tenantIds.ToArray());
            }

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
            CancellationToken token)
            => DetectForTenantsAsync(tenantIds, token);
    }

    // A detector for a store WITHOUT per-tenant event partitioning — SupportsTenantPartitioning stays on
    // its interface default of false, so the daemon never builds a TenantedHighWaterCoordinator.
    private sealed class GlobalOnlyStubDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }
}
