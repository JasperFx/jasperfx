using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Shouldly;
using RebuildHarness = EventTests.Daemon.CrossProductRebuildCapTests.RebuildHarness;

namespace EventTests.Daemon;

// A rebuild stops the projection's continuous agents and leaves restarting them to whoever runs the daemon
// (jasperfx#535): the projection coordinator on a Solo or HotCold host, the driving caller everywhere else. The
// coordinator restarts a shard that has no registered agent, or one Paused for longer than HealthCheckPollingTime,
// and leaves a Stopped one alone. rebuildAgent registered each replay agent under its shard's identity and the
// rebuild then drained it, so a rebuilt shard stayed registered as Stopped and the coordinator never started it
// again: new events went unprojected until the process restarted.
//
// These drive the real daemon under the real coordinator loop (ProjectionCoordinatorBase over the
// SoloProjectionDistributor), over substituted storage.
public class RebuiltShardResumeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    // How long the coordinator gets to resume a rebuilt shard. Its loop polls every 50ms here, so this is a couple
    // of hundred passes.
    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(10);

    private static readonly ShardName Trips = new("Trips");

    [Fact]
    public async Task the_coordinator_resumes_a_rebuilt_shard_and_it_projects_an_event_appended_after_the_rebuild()
    {
        await using var harness = new ResumeHarness(highWater: 5);
        await harness.Coordinator.StartAsync(CancellationToken.None);

        var before = await harness.WaitForPageAsync(ShardExecutionMode.Continuous, sequence: 5)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await harness.Daemon.RebuildProjectionAsync("Trips", TestTimeout, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        // The rebuild really replayed the projection
        harness.Pages.ShouldContain(x => x.Mode == ShardExecutionMode.Rebuild && x.Sequences.Contains(5L));

        // An event appended after the rebuild
        var resumed = harness.WaitForPageAsync(ShardExecutionMode.Continuous, sequence: 6);
        harness.Detector.Mark = 6;

        var page = (await withinAsync(resumed, ResumeTimeout)).ShouldNotBeNull(
            $"No continuous agent projected the event appended after the rebuild within {ResumeTimeout}: {describe(harness.Daemon, Trips)}");

        page.Agent.ShouldNotBeSameAs(before.Agent);

        // A start registers its agent just after starting it, so the page can land a moment before the registry
        // shows it
        var registered = (await runningAgentAsync(harness.Daemon, Trips, TestTimeout)).ShouldNotBeNull();
        registered.ShouldBeSameAs(page.Agent);
        harness.Daemon.CurrentAgents().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_coordinator_resumes_only_the_rebuilt_tenants_shard()
    {
        await using var harness = new RebuildHarness(["t1", "t2"], ["ProjA"], gated: false);

        var t1 = new ShardName("ProjA").ForTenant("t1");
        var t2 = new ShardName("ProjA").ForTenant("t2");

        var coordinator = new SingleDaemonCoordinator(soloDistributor([t1, t2]), harness.Daemon);
        await coordinator.StartAsync(CancellationToken.None);

        try
        {
            var t1Before = (await runningAgentAsync(harness.Daemon, t1, TestTimeout)).ShouldNotBeNull();
            var t2Before = (await runningAgentAsync(harness.Daemon, t2, TestTimeout)).ShouldNotBeNull();

            await harness.Daemon.RebuildProjectionAsync("ProjA", "t1", TestTimeout, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            (await runningAgentAsync(harness.Daemon, t1, ResumeTimeout, except: t1Before)).ShouldNotBeNull(
                $"The coordinator did not resume the rebuilt tenant's shard within {ResumeTimeout}: {describe(harness.Daemon, t1)}");

            // The other tenant's agent kept running through the rebuild, untouched
            harness.Daemon.CurrentAgents().Single(x => x.Name.Identity == t2.Identity).ShouldBeSameAs(t2Before);
            t2Before.Status.ShouldBe(AgentStatus.Running);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task without_a_coordinator_a_rebuilt_shard_stays_stopped_until_its_caller_restarts_it()
    {
        await using var harness = new ResumeHarness(highWater: 5);
        await harness.Daemon.StartAllAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await harness.WaitForPageAsync(ShardExecutionMode.Continuous, sequence: 5)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await harness.Daemon.RebuildProjectionAsync("Trips", TestTimeout, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        // Nothing restarts it on a host with no coordinator, and the drained replay agent is no longer registered
        // under the shard's name
        harness.Daemon.StatusFor(Trips.Identity).ShouldBe(AgentStatus.Stopped);
        harness.Daemon.CurrentAgents().ShouldBeEmpty();

        // The driving caller resumes it through the registered start path, as Wolverine's EventSubscriptionAgent does
        var resumed = harness.WaitForPageAsync(ShardExecutionMode.Continuous, sequence: 6);
        harness.Detector.Mark = 6;

        var agent = await harness.Daemon.StartAgentAsync(Trips, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        agent.Status.ShouldBe(AgentStatus.Running);
        (await resumed.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Agent.ShouldBeSameAs(agent);
    }

    private static async Task<T?> withinAsync<T>(Task<T> task, TimeSpan timeout) where T : class
    {
        try
        {
            return await task.WaitAsync(timeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    // Polls the daemon's registry until the shard has a Running agent other than `except`, or the timeout passes
    private static async Task<ISubscriptionAgent?> runningAgentAsync(IProjectionDaemon daemon, ShardName shard,
        TimeSpan timeout, ISubscriptionAgent? except = null)
    {
        using var expiry = new CancellationTokenSource(timeout);
        while (!expiry.IsCancellationRequested)
        {
            var agent = daemon.CurrentAgents().FirstOrDefault(x => x.Name.Identity == shard.Identity);
            if (agent is { Status: AgentStatus.Running } && !ReferenceEquals(agent, except))
            {
                return agent;
            }

            await Task.Delay(20, CancellationToken.None);
        }

        return null;
    }

    private static string describe(IProjectionDaemon daemon, ShardName shard)
    {
        var registered = daemon.CurrentAgents().FirstOrDefault(x => x.Name.Identity == shard.Identity);
        return registered == null
            ? $"{shard.Identity} has no registered agent"
            : $"{shard.Identity} is registered with a {registered.Status} agent";
    }

    private static SoloProjectionDistributor soloDistributor(IReadOnlyList<ShardName> names)
    {
        var database = new ProjectionDatabase("db1", new Uri("fake://db1"));
        return new SoloProjectionDistributor(
            () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { database }),
            () => names,
            (db, shards, lockId) => new ProjectionSet(db, shards, lockId),
            baseLockId: 1);
    }

    private sealed record ProjectionDatabase(string Identifier, Uri DatabaseUri) : IProjectionDatabase;

    private sealed record ProjectionSet(IProjectionDatabase Database, IReadOnlyList<ShardName> Names, int LockId)
        : IProjectionSet;

    // The coordinator loop over one daemon, polling fast so a test waits milliseconds rather than seconds
    private sealed class SingleDaemonCoordinator(IProjectionDistributor distributor, IProjectionDaemon daemon)
        : ProjectionCoordinatorBase(distributor, NullLogger.Instance, ResiliencePipeline.Empty, TimeProvider.System,
            leadershipPollingTime: TimeSpan.FromMilliseconds(50), agentPauseTime: TimeSpan.FromMilliseconds(50),
            healthCheckPollingTime: TimeSpan.FromSeconds(5))
    {
        protected override IProjectionDaemon ResolveDaemon(IProjectionSet set) => daemon;

        protected override IReadOnlyList<IProjectionDaemon> ResolvedDaemons() => new[] { daemon };

        protected override void ClearResolvedDaemons()
        {
            // The one daemon is the test's, not a cache's
        }

        public override IProjectionDaemon DaemonForMainDatabase() => daemon;

        public override ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier) => new(daemon);

        public override ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
            => new(new IProjectionDaemon[] { daemon });
    }

    // What one agent's execution was handed: which agent, in which mode, and the events on the page
    private sealed record Page(ISubscriptionAgent Agent, ShardExecutionMode Mode, long[] Sequences);

    // A real JasperFxAsyncDaemon over a substituted store and database, with a single-shard projection whose
    // execution records every page and persists its ceiling as the shard's progression, and a high-water mark the
    // test moves to "append" an event.
    private sealed class ResumeHarness : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, long> _progress = new();
        private readonly List<Page> _pages = [];
        private readonly List<(Func<Page, bool> Match, TaskCompletionSource<Page> Completion)> _waiters = [];

        public ResumeHarness(long highWater)
        {
            Detector = new MovableDetector { Mark = highWater };

            var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            store.Meter.Returns(new Meter("tests"));
            store.TimeProvider.Returns(TimeProvider.System);
            store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            store.RebuildErrors.Returns(new ErrorHandlingOptions());

            var loader = new DenseEventLoader();
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(loader);
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

            var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection, Trips,
                new RecordingFactory(this), new EventFilterable());
            store.AllShards().Returns([shard]);

            var database = Substitute.For<IEventDatabase>();
            database.Identifier.Returns("db1");
            database.DatabaseUri.Returns(new Uri("fake://db1"));
            database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));
            database.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns((long?)null);
            database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .Returns(info => Task.FromResult(_progress.GetValueOrDefault(info.Arg<ShardName>().Identity)));

            var source = Substitute.For<IProjectionSource<FakeOperations, FakeSession>>();
            source.Name.Returns(Trips.Name);
            source.Shards().Returns([shard]);

            var projections = new CrossProductRebuildCapTests.FakeProjectionGraph
            {
                MaxConcurrentEventLoadsPerDatabase = 0,
                FastPollingTime = TimeSpan.FromMilliseconds(50),
                SlowPollingTime = TimeSpan.FromMilliseconds(50)
            };
            projections.All.Add(source);

            Daemon = new CrossProductRebuildCapTests.CoordinatedDaemon(store, database, Detector, projections);

            Coordinator = new SingleDaemonCoordinator(soloDistributor([Trips]), Daemon);
        }

        public MovableDetector Detector { get; }

        public CrossProductRebuildCapTests.CoordinatedDaemon Daemon { get; }

        public SingleDaemonCoordinator Coordinator { get; }

        public IReadOnlyList<Page> Pages
        {
            get
            {
                lock (_pages)
                {
                    return _pages.ToArray();
                }
            }
        }

        // Completes with the first page, recorded already or from now on, that carried the sequence in that mode
        public Task<Page> WaitForPageAsync(ShardExecutionMode mode, long sequence)
        {
            Func<Page, bool> match = x => x.Mode == mode && x.Sequences.Contains(sequence);

            lock (_pages)
            {
                var recorded = _pages.FirstOrDefault(match);
                if (recorded != null) return Task.FromResult(recorded);

                var completion = new TaskCompletionSource<Page>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((match, completion));
                return completion.Task;
            }
        }

        public void Record(Page page, long ceiling)
        {
            // Progression is persisted before the page is announced, as a store's projection batch does
            _progress[page.Agent.Name.Identity] = ceiling;

            lock (_pages)
            {
                _pages.Add(page);

                foreach (var waiter in _waiters.Where(x => x.Match(page)).ToArray())
                {
                    _waiters.Remove(waiter);
                    waiter.Completion.TrySetResult(page);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Stops the loop, then stops and disposes the daemon
            await Coordinator.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingFactory(ResumeHarness harness) : ISubscriptionFactory<FakeOperations, FakeSession>
    {
        public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
            IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
            => new RecordingExecution(harness, shardName);

        public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
            IEventDatabase database, ILogger logger, ShardName shardName)
            => new RecordingExecution(harness, shardName);
    }

    // One per agent, as a store builds them. The agent sets Mode before it hands over a page.
    private sealed class RecordingExecution(ResumeHarness harness, ShardName shardName) : ISubscriptionExecution
    {
        public ShardName ShardName { get; } = shardName;

        public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
        {
            harness.Record(new Page(subscriptionAgent, Mode, page.Select(x => x.Sequence).ToArray()), page.Ceiling);
            return subscriptionAgent.MarkSuccessAsync(page.Ceiling);
        }

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

    // Serves every sequence in (floor, high water], one event each, capped at the batch size
    private sealed class DenseEventLoader : IEventLoader
    {
        public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            var page = new EventPage(request.Floor);
            var top = Math.Min(request.Floor + request.BatchSize, request.HighWater);
            for (var sequence = request.Floor + 1; sequence <= top; sequence++)
            {
                page.Add(new Event<AEvent>(new AEvent()) { Sequence = sequence });
            }

            page.CalculateCeiling(request.BatchSize, request.HighWater);
            return Task.FromResult(page);
        }
    }

    // A non-partitioned store's high water, which the test raises to append an event
    private sealed class MovableDetector : IHighWaterDetector
    {
        private long _mark;

        public long Mark
        {
            get => Interlocked.Read(ref _mark);
            set => Interlocked.Exchange(ref _mark, value);
        }

        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
        {
            var mark = Mark;
            return Task.FromResult(new HighWaterStatistics
            {
                CurrentMark = mark, LastMark = mark, HighestSequence = mark
            });
        }

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }
}
