using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Shouldly;

namespace EventTests.Daemon;

public class ProjectionCoordinatorBaseTests
{
    private static readonly ShardName TheShard = new("Trip", "All", 1);

    private static TestCoordinator BuildCoordinator(FakeDistributor distributor, FakeDaemon daemon)
    {
        return new TestCoordinator(
            distributor,
            daemon,
            leadershipPollingTime: TimeSpan.FromSeconds(30),
            agentPauseTime: TimeSpan.FromSeconds(30),
            healthCheckPollingTime: TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task starts_agents_for_a_set_we_already_hold_the_lock_for()
    {
        var set = new FakeProjectionSet("db1", [TheShard], 100);
        var daemon = new FakeDaemon();
        var distributor = new FakeDistributor([set]) { LockHeld = true };

        var coordinator = BuildCoordinator(distributor, daemon);
        await coordinator.StartAsync(CancellationToken.None);

        await WaitFor(() => daemon.Started.Contains(TheShard.Identity));

        await coordinator.StopAsync(CancellationToken.None);

        daemon.Started.ShouldContain(TheShard.Identity);
    }

    // The behavioral fix from #326: Polecat called StartAgentAsync raw — no
    // resilience, no lock-release-on-failure. The shared loop adopts Marten's
    // resilient tryStartAgent, so a failed agent start now releases the set's lock
    // (and ejects a paused shard) so another node can pick the set up.
    [Fact]
    public async Task releases_lock_when_agent_start_fails()
    {
        var set = new FakeProjectionSet("db1", [TheShard], 100);
        var daemon = new FakeDaemon { ThrowOnStart = true, StatusAfterFailure = AgentStatus.Paused };
        var distributor = new FakeDistributor([set]) { LockHeld = true };

        var coordinator = BuildCoordinator(distributor, daemon);
        await coordinator.StartAsync(CancellationToken.None);

        await WaitFor(() => distributor.ReleasedLocks.Contains(set));

        await coordinator.StopAsync(CancellationToken.None);

        distributor.ReleasedLocks.ShouldContain(set);
        daemon.Ejected.ShouldContain(TheShard.Identity);
    }

    [Fact]
    public async Task stops_agents_for_a_set_whose_lock_we_could_not_attain()
    {
        var set = new FakeProjectionSet("db1", [TheShard], 100);
        // We don't hold the lock and can't attain it — a set we used to run must be stopped.
        var daemon = new FakeDaemon { Status = AgentStatus.Running };
        var distributor = new FakeDistributor([set]) { LockHeld = false, CanAttain = false };

        var coordinator = BuildCoordinator(distributor, daemon);
        await coordinator.StartAsync(CancellationToken.None);

        await WaitFor(() => daemon.Stopped.Contains(TheShard.Identity));

        await coordinator.StopAsync(CancellationToken.None);

        daemon.Stopped.ShouldContain(TheShard.Identity);
    }

    [Fact]
    public async Task stop_releases_all_locks_and_disposes_daemons()
    {
        var daemon = new FakeDaemon();
        var distributor = new FakeDistributor([]);

        var coordinator = BuildCoordinator(distributor, daemon);
        await coordinator.StartAsync(CancellationToken.None);
        // Touch the cache so there is a daemon to dispose / stop.
        coordinator.ForceResolve();

        await coordinator.StopAsync(CancellationToken.None);

        distributor.ReleaseAllLocksCount.ShouldBeGreaterThanOrEqualTo(1);
        daemon.DisposeCount.ShouldBeGreaterThanOrEqualTo(1);
        daemon.StopAllCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var elapsed = 0;
        while (!condition() && elapsed < timeoutMs)
        {
            await Task.Delay(25);
            elapsed += 25;
        }
    }

    private sealed class TestCoordinator : ProjectionCoordinatorBase
    {
        private readonly FakeDaemon _daemon;

        public TestCoordinator(IProjectionDistributor distributor, FakeDaemon daemon,
            TimeSpan leadershipPollingTime, TimeSpan agentPauseTime, TimeSpan healthCheckPollingTime)
            : base(distributor, NullLogger.Instance, ResiliencePipeline.Empty, TimeProvider.System,
                leadershipPollingTime, agentPauseTime, healthCheckPollingTime)
        {
            _daemon = daemon;
        }

        private bool _resolved;

        public void ForceResolve() => _resolved = true;

        protected override IProjectionDaemon ResolveDaemon(IProjectionSet set)
        {
            _resolved = true;
            return _daemon;
        }

        protected override IReadOnlyList<IProjectionDaemon> ResolvedDaemons()
            => _resolved ? [_daemon] : [];

        public override IProjectionDaemon DaemonForMainDatabase() => _daemon;

        public override ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
            => new(_daemon);

        public override ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
            => new(new IProjectionDaemon[] { _daemon });
    }

    private sealed class FakeProjectionSet(string id, IReadOnlyList<ShardName> names, int lockId) : IProjectionSet
    {
        public int LockId { get; } = lockId;
        public IProjectionDatabase Database { get; } = new FakeDatabase(id);
        public IReadOnlyList<ShardName> Names { get; } = names;
    }

    private sealed class FakeDatabase(string id) : IProjectionDatabase
    {
        public string Identifier { get; } = id;
        public Uri DatabaseUri { get; } = new($"fake://{id}");
    }

    private sealed class FakeDistributor(IReadOnlyList<IProjectionSet> sets) : IProjectionDistributor
    {
        public bool LockHeld { get; init; }
        public bool CanAttain { get; init; } = true;
        public List<IProjectionSet> ReleasedLocks { get; } = [];
        public int ReleaseAllLocksCount { get; private set; }

        public ValueTask<IReadOnlyList<IProjectionSet>> BuildDistributionAsync() => new(sets);
        public Task RandomWait(CancellationToken token) => Task.CompletedTask;
        public bool HasLock(IProjectionSet set) => LockHeld;
        public Task<bool> TryAttainLockAsync(IProjectionSet set, CancellationToken token) => Task.FromResult(CanAttain);

        public Task ReleaseLockAsync(IProjectionSet set)
        {
            ReleasedLocks.Add(set);
            return Task.CompletedTask;
        }

        public Task ReleaseAllLocks()
        {
            ReleaseAllLocksCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // IProjectionDaemon is large; the coordinator loop only touches the members
    // implemented here. The rest throw so an accidental new dependency surfaces loudly.
    private sealed class FakeDaemon : IProjectionDaemon
    {
        public bool ThrowOnStart { get; init; }
        public AgentStatus Status { get; init; } = AgentStatus.Stopped;
        public AgentStatus StatusAfterFailure { get; init; } = AgentStatus.Stopped;

        public List<string> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public List<string> Ejected { get; } = [];
        public int DisposeCount { get; private set; }
        public int StopAllCount { get; private set; }

        private bool _failed;

        public Task StartAgentAsync(string shardName, CancellationToken token)
        {
            if (ThrowOnStart)
            {
                _failed = true;
                throw new InvalidOperationException("boom");
            }

            Started.Add(shardName);
            return Task.CompletedTask;
        }

        public Task StopAgentAsync(string shardName, Exception? ex = null)
        {
            Stopped.Add(shardName);
            return Task.CompletedTask;
        }

        public AgentStatus StatusFor(string shardName)
        {
            if (_failed) return StatusAfterFailure;
            return Status;
        }

        public IReadOnlyList<ISubscriptionAgent> CurrentAgents() => [];
        public bool HasAnyPaused() => false;

        public void EjectPausedShard(string shardName) => Ejected.Add(shardName);

        public Task StopAllAsync()
        {
            StopAllCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;

        // ---- Unused by the coordinator loop ----
        public Task PrepareForRebuildsAsync() => throw new NotSupportedException();
        public ShardStateTracker Tracker => throw new NotSupportedException();
        public bool IsRunning => throw new NotSupportedException();
        public Task RebuildProjectionAsync(string projectionName, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync<TView>(CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync(Type projectionType, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync(Type projectionType, TimeSpan shardTimeout, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync(string projectionName, TimeSpan shardTimeout, CancellationToken token) => throw new NotSupportedException();
        public Task RebuildProjectionAsync<TView>(TimeSpan shardTimeout, CancellationToken token) => throw new NotSupportedException();
        public Task<ISubscriptionAgent> StartAgentAsync(ShardName name, CancellationToken token) => throw new NotSupportedException();
        public Task StopAgentAsync(ShardName shardName, Exception? ex = null) => throw new NotSupportedException();
        public Task StartAllAsync() => throw new NotSupportedException();
        public Task CatchUpAsync(CancellationToken cancellation) => throw new NotSupportedException();
        public Task CatchUpAsync(TimeSpan timeout, CancellationToken cancellation) => throw new NotSupportedException();
        public Task WaitForNonStaleData(TimeSpan timeout) => throw new NotSupportedException();
        public long HighWaterMark() => throw new NotSupportedException();
        public Task WaitForShardToBeRunning(string shardName, TimeSpan timeout) => throw new NotSupportedException();
        public Task RewindSubscriptionAsync(string subscriptionName, CancellationToken token, long? sequenceFloor = 0, DateTimeOffset? timestamp = null) => throw new NotSupportedException();
    }
}
