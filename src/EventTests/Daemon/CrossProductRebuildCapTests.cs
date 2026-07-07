using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#497 (the #420 leftover) acceptance: ONE shared per-database budget bounds the rebuild
// cell fan-out across BOTH layers — concurrent projection-level rebuilds AND each projection's
// per-(tenant, shard) cross product — so `projections rebuild --max-concurrent N` on a
// tenant-partitioned store really means "at most N concurrently replaying cells per database",
// never N x tenants. These tests drive the REAL JasperFxAsyncDaemon (real SubscriptionAgents,
// substituted store/database) with an instrumented, TCS-gated event loader: every rebuild cell
// blocks inside its LoadAsync until the test releases it, so an over-admitted cell is caught
// deterministically (it would show up as an extra concurrent load while the gates are closed)
// rather than by timing.
public class CrossProductRebuildCapTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static string[] tenantsNamed(int count)
        => Enumerable.Range(1, count).Select(i => $"t{i}").ToArray();

    private static string[] expectedCells(string[] projections, string[] tenants)
        => projections.SelectMany(p => tenants.Select(t => $"{p}:All:{t}")).OrderBy(x => x).ToArray();

    [Fact]
    public async Task cells_across_projections_and_tenants_never_exceed_and_actually_reach_the_cap()
    {
        // The issue's scenario shrunk to test size: 2 projections x 6 tenants = 12 rebuild cells,
        // budget 3, both projections rebuilding concurrently (the CLI's projection-level layer).
        // Without a SHARED budget, layer multiplication would admit up to 6 concurrent cells
        // (projection-level 2 x per-projection 3); without any cap, all 12.
        var tenants = tenantsNamed(6);
        await using var harness = new RebuildHarness(tenants, ["ProjA", "ProjB"],
            settings => settings.MaxConcurrentRebuildsPerDatabase = 3);

        var rebuilds = Task.WhenAll(
            harness.Daemon.RebuildProjectionAsync("ProjA", tenantId: null, TestTimeout, CancellationToken.None),
            harness.Daemon.RebuildProjectionAsync("ProjB", tenantId: null, TestTimeout, CancellationToken.None));

        // Hold the first 3 gates until all 3 cells are simultaneously blocked inside LoadAsync —
        // that forces the observed peak to actually REACH the cap instead of passing vacuously.
        var held = new List<TaskCompletionSource>();
        for (var i = 0; i < 3; i++)
        {
            held.Add(await harness.Loader.NextEntryAsync());
        }

        harness.Loader.CurrentlyLoading.ShouldBe(3);

        foreach (var gate in held)
        {
            gate.SetResult();
        }

        // Release the remaining cells as they arrive.
        for (var i = 0; i < 9; i++)
        {
            (await harness.Loader.NextEntryAsync()).SetResult();
        }

        await rebuilds.WaitAsync(TestTimeout);

        harness.Loader.PeakConcurrency.ShouldBe(3);
        harness.Loader.CellsLoaded.OrderBy(x => x)
            .ShouldBe(expectedCells(["ProjA", "ProjB"], tenants));
    }

    [Fact]
    public async Task the_cli_override_path_replaces_the_budget()
    {
        // No DaemonSettings knob and no store default -> the daemon starts with no budget. The
        // ProjectionHost --max-concurrent path assigns the resolved cap straight onto the daemon;
        // subsequent rebuilds must honor the NEW budget.
        var tenants = tenantsNamed(5);
        await using var harness = new RebuildHarness(tenants, ["ProjA"]);

        harness.Daemon.MaxConcurrentRebuildsPerDatabase.ShouldBeNull();
        harness.Daemon.MaxConcurrentRebuildsPerDatabase = 2;
        harness.Daemon.MaxConcurrentRebuildsPerDatabase.ShouldBe(2);

        var rebuild = harness.Daemon
            .RebuildProjectionAsync("ProjA", tenantId: null, TestTimeout, CancellationToken.None);

        var held = new List<TaskCompletionSource>
        {
            await harness.Loader.NextEntryAsync(),
            await harness.Loader.NextEntryAsync()
        };

        harness.Loader.CurrentlyLoading.ShouldBe(2);

        foreach (var gate in held)
        {
            gate.SetResult();
        }

        for (var i = 0; i < 3; i++)
        {
            (await harness.Loader.NextEntryAsync()).SetResult();
        }

        await rebuild.WaitAsync(TestTimeout);

        harness.Loader.PeakConcurrency.ShouldBe(2);
        harness.Loader.CellsLoaded.OrderBy(x => x).ShouldBe(expectedCells(["ProjA"], tenants));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task a_non_positive_cap_is_unbounded_and_still_runs_every_cell_exactly_once(int cap)
    {
        // ParallelOptions.MaxDegreeOfParallelism = 0 throws — a non-positive budget must never
        // reach it. Unbounded keeps the historical sequential per-tenant walk, so the loader runs
        // ungated here (a held gate would deadlock a serialized walk, by design of the gating).
        var tenants = tenantsNamed(4);
        await using var harness = new RebuildHarness(tenants, ["ProjA", "ProjB"], gated: false);

        harness.Daemon.MaxConcurrentRebuildsPerDatabase = cap;

        await Task.WhenAll(
                harness.Daemon.RebuildProjectionAsync("ProjA", tenantId: null, TestTimeout, CancellationToken.None),
                harness.Daemon.RebuildProjectionAsync("ProjB", tenantId: null, TestTimeout, CancellationToken.None))
            .WaitAsync(TestTimeout);

        harness.Loader.CellsLoaded.OrderBy(x => x)
            .ShouldBe(expectedCells(["ProjA", "ProjB"], tenants));
    }

    [Fact]
    public async Task an_unset_cap_keeps_the_historical_behavior_and_runs_every_cell_exactly_once()
    {
        var tenants = tenantsNamed(3);
        await using var harness = new RebuildHarness(tenants, ["ProjA"], gated: false);

        await harness.Daemon
            .RebuildProjectionAsync("ProjA", tenantId: null, TestTimeout, CancellationToken.None)
            .WaitAsync(TestTimeout);

        harness.Loader.CellsLoaded.OrderBy(x => x).ShouldBe(expectedCells(["ProjA"], tenants));
    }

    [Theory]
    [InlineData(2, 8, 2)] // explicit maxParallelism always wins over the daemon budget
    [InlineData(0, 8, 0)] // explicit non-positive = unbounded, even with a budget configured
    [InlineData(-1, null, -1)]
    [InlineData(null, 8, 8)] // jasperfx#497: default follows the daemon's shared budget
    [InlineData(null, -1, -1)] // a daemon explicitly set unbounded stays unbounded
    [InlineData(null, null, 4)] // no signal anywhere -> the #496 default of 4
    public void rebuild_everywhere_launch_width_resolution(int? maxParallelism, int? daemonBudget, int expected)
    {
        CrossTenantRebuild.ResolveLaunchWidth(maxParallelism, daemonBudget).ShouldBe(expected);
    }

    [Fact]
    public async Task rebuild_everywhere_defaults_its_fan_out_to_the_daemon_budget()
    {
        // jasperfx#496 gave RebuildEverywhereAsync a fixed default of 4; #497 keeps it consistent
        // with the shared per-database budget instead whenever one is configured.
        var daemon = new GatedRecordingDaemon { MaxConcurrentRebuildsPerDatabase = 2 };

        var source = Substitute.For<ICrossTenantRebuildSource>();
        var tenants = tenantsNamed(5);
        source.FindRebuildTenantsAsync("Orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(tenants));

        var rebuild = new CrossTenantRebuild(source)
            .RebuildEverywhereAsync(daemon, "Orders", TestTimeout, CancellationToken.None);

        var held = new List<TaskCompletionSource>
        {
            await daemon.NextEntryAsync(),
            await daemon.NextEntryAsync()
        };

        daemon.CurrentlyRebuilding.ShouldBe(2);

        foreach (var gate in held)
        {
            gate.SetResult();
        }

        for (var i = 0; i < 3; i++)
        {
            (await daemon.NextEntryAsync()).SetResult();
        }

        var rebuilt = await rebuild.WaitAsync(TestTimeout);

        rebuilt.OrderBy(x => x).ShouldBe(tenants.OrderBy(x => x));
        daemon.PeakConcurrency.ShouldBe(2);
        daemon.RebuiltTenants.OrderBy(x => x).ShouldBe(tenants.OrderBy(x => x));
    }

    // ---------------------------------------------------------------------------------------------
    // Harness: a REAL JasperFxAsyncDaemon over a substituted store + database. The database also
    // implements ICrossTenantRebuildSource and the detector supports tenant partitioning, so a
    // store-global RebuildProjectionAsync fans out the per-(tenant, shard) cross product exactly the
    // way Marten/Polecat do under per-tenant event partitioning.
    // ---------------------------------------------------------------------------------------------

    private sealed class RebuildHarness : IAsyncDisposable
    {
        public RebuildHarness(string[] tenants, string[] projectionNames,
            Action<DaemonSettings>? configureSettings = null, bool gated = true)
        {
            Loader = new GatedInstrumentedLoader(gated);

            var detector = new StubPartitionedDetector();
            foreach (var tenantId in tenants)
            {
                detector.SetTenantMark(tenantId, 42);
            }

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

            // The database doubles as the cross-tenant rebuild source, like Marten's
            // MartenDatabase under per-tenant partitioning.
            Database = Substitute.For<IEventDatabase, ICrossTenantRebuildSource>();
            Database.Identifier.Returns("db1");
            Database.DatabaseUri.Returns(new Uri("fake://db1"));
            Database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));
            ((ICrossTenantRebuildSource)Database)
                .FindRebuildTenantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<string>>(tenants));

            var projections = new FakeProjectionGraph
            {
                // Keep the jasperfx#494 load throttle out of the way — the loader IS the
                // instrumentation point and must observe only the rebuild budget.
                MaxConcurrentEventLoadsPerDatabase = 0
            };
            configureSettings?.Invoke(projections);

            foreach (var name in projectionNames)
            {
                var factory = new CompletingSubscriptionFactory();
                var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
                    new ShardName(name), factory, new EventFilterable());

                var source = Substitute.For<IProjectionSource<FakeOperations, FakeSession>>();
                source.Name.Returns(name);
                source.Shards().Returns([shard]);

                projections.All.Add(source);
            }

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                Store, Database, new NulloLogger(), detector, projections);
        }

        public IEventStore<FakeOperations, FakeSession> Store { get; }
        public IEventDatabase Database { get; }
        public GatedInstrumentedLoader Loader { get; }
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAllAsync();
            Daemon.Dispose();
        }
    }

    // Minimal concrete ProjectionGraph — the daemon consumes it as DaemonSettings plus the
    // registered projection sources in All.
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

    // The instrumentation heart: every rebuild cell's single LoadAsync increments a live counter
    // (tracking the peak), parks on a TCS gate the test hands out through an unbounded channel, and
    // only then returns an already-caught-up page. Cells therefore stay INSIDE the instrumented
    // region until the test releases them — an over-admitted cell deterministically raises the
    // observed concurrency while the gates are closed, no timing required.
    private sealed class GatedInstrumentedLoader : IEventLoader
    {
        private readonly bool _gated;
        private readonly Channel<TaskCompletionSource> _entries = Channel.CreateUnbounded<TaskCompletionSource>();
        private int _current;
        private int _peak;

        public GatedInstrumentedLoader(bool gated)
        {
            _gated = gated;
        }

        public ConcurrentBag<string> CellsLoaded { get; } = new();

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public int CurrentlyLoading => Volatile.Read(ref _current);

        public async Task<TaskCompletionSource> NextEntryAsync()
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            return await _entries.Reader.ReadAsync(timeout.Token);
        }

        public async Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            var now = Interlocked.Increment(ref _current);
            int snapshot;
            while (now > (snapshot = Volatile.Read(ref _peak)))
            {
                Interlocked.CompareExchange(ref _peak, now, snapshot);
            }

            CellsLoaded.Add(request.Name.Identity);

            if (_gated)
            {
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _entries.Writer.TryWrite(gate);
                await gate.Task.WaitAsync(TestTimeout, token);
            }

            Interlocked.Decrement(ref _current);

            // An already-caught-up page: ceiling == the request's high water, zero events. The
            // completing execution below acknowledges it, which finishes the cell's replay.
            var page = new EventPage(request.Floor);
            page.CalculateCeiling(request.BatchSize, request.HighWater);
            return page;
        }
    }

    private sealed class CompletingSubscriptionFactory : ISubscriptionFactory<FakeOperations, FakeSession>
    {
        public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
            IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
            => new CompletingExecution(shardName);

        public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
            IEventDatabase database, ILogger logger, ShardName shardName)
            => new CompletingExecution(shardName);
    }

    // Acknowledges every enqueued page immediately, which posts RangeCompleted back to the agent and
    // completes the rebuild replay once the page ceiling reaches the cell's high-water ceiling.
    private sealed class CompletingExecution : ISubscriptionExecution
    {
        public CompletingExecution(ShardName shardName)
        {
            ShardName = shardName;
        }

        public ShardName ShardName { get; }

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Rebuild;

        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
            => subscriptionAgent.MarkSuccessAsync(page.Ceiling);

        public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;

        public Task HardStopAsync() => Task.CompletedTask;

        public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
        {
            executor = null;
            return false;
        }

        public Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
            CancellationToken cancellation) => Task.CompletedTask;

        public Task ProcessRangeAsync(EventRange range) => Task.CompletedTask;

        public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
        {
            caching = null;
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A hand-rolled IProjectionDaemon for the RebuildEverywhereAsync consistency test: per-tenant
    // rebuild calls park on TCS gates handed to the test (same protocol as the loader above) while
    // an Interlocked pair tracks live/peak concurrency. Only the members the cross-tenant fan-out
    // touches are implemented; the rest throw so an accidental new dependency surfaces loudly.
    private sealed class GatedRecordingDaemon : IProjectionDaemon
    {
        private readonly Channel<TaskCompletionSource> _entries = Channel.CreateUnbounded<TaskCompletionSource>();
        private int _current;
        private int _peak;

        public int? MaxConcurrentRebuildsPerDatabase { get; set; }

        public ConcurrentBag<string> RebuiltTenants { get; } = new();

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public int CurrentlyRebuilding => Volatile.Read(ref _current);

        public async Task<TaskCompletionSource> NextEntryAsync()
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            return await _entries.Reader.ReadAsync(timeout.Token);
        }

        public async Task RebuildProjectionAsync(string projectionName, string? tenantId, TimeSpan shardTimeout,
            CancellationToken token)
        {
            var now = Interlocked.Increment(ref _current);
            int snapshot;
            while (now > (snapshot = Volatile.Read(ref _peak)))
            {
                Interlocked.CompareExchange(ref _peak, now, snapshot);
            }

            RebuiltTenants.Add(tenantId!);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _entries.Writer.TryWrite(gate);
            await gate.Task.WaitAsync(TestTimeout, token);

            Interlocked.Decrement(ref _current);
        }

        public void Dispose()
        {
        }

        // ---- Unused by the cross-tenant fan-out ----
        public Task PrepareForRebuildsAsync() => throw new NotSupportedException();
        public ShardStateTracker Tracker => throw new NotSupportedException();
        public bool IsRunning => throw new NotSupportedException();
        public Task RebuildProjectionAsync(string projectionName, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync<TView>(CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync(Type projectionType, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync(Type projectionType, TimeSpan shardTimeout, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync(string projectionName, TimeSpan shardTimeout, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync<TView>(TimeSpan shardTimeout, CancellationToken token) => throw new NotSupportedException();
        public Task StartAgentAsync(string shardName, CancellationToken token) => throw new NotSupportedException();
        public Task<ISubscriptionAgent> StartAgentAsync(ShardName name, CancellationToken token) => throw new NotSupportedException();
        public Task StopAgentAsync(string shardName, Exception? ex = null) => throw new NotSupportedException();
        public Task StopAgentAsync(ShardName shardName, Exception? ex = null) => throw new NotSupportedException();
        public Task StartAllAsync() => throw new NotSupportedException();
        public Task StopAllAsync() => throw new NotSupportedException();
        public Task CatchUpAsync(CancellationToken cancellation) => throw new NotSupportedException();
        public Task CatchUpAsync(TimeSpan timeout, CancellationToken cancellation) => throw new NotSupportedException();
        public Task WaitForNonStaleData(TimeSpan timeout) => throw new NotSupportedException();
        public long HighWaterMark() => throw new NotSupportedException();
        public Task WaitForShardToBeRunning(string shardName, TimeSpan timeout) => throw new NotSupportedException();
        public Task RewindSubscriptionAsync(string subscriptionName, CancellationToken token, long? sequenceFloor = 0, DateTimeOffset? timestamp = null) => throw new NotSupportedException();
        public IReadOnlyList<ISubscriptionAgent> CurrentAgents() => throw new NotSupportedException();
        public bool HasAnyPaused() => throw new NotSupportedException();
        public void EjectPausedShard(string shardName) => throw new NotSupportedException();
        public AgentStatus StatusFor(string shardName) => throw new NotSupportedException();
    }

    // A detector for a store WITH per-tenant event partitioning — the store-global Detect() stays
    // pinned at mark 0 so any nonzero rebuild ceiling can only come from the per-tenant coordinator.
    // (Same stub shape as PerTenantStartAgentAsyncTests.)
    private sealed class StubPartitionedDetector : IHighWaterDetector
    {
        private readonly Dictionary<string, long> _marks = new();

        public Uri DatabaseUri { get; } = new("fake://db1");

        public bool SupportsTenantPartitioning => true;

        public void SetTenantMark(string tenantId, long mark)
        {
            lock (_marks)
            {
                _marks[tenantId] = mark;
            }
        }

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
        {
            lock (_marks)
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
        }

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token)
            => DetectForTenantsAsync(tenantIds, token);
    }
}
