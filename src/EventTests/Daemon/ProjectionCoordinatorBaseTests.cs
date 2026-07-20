using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Shouldly;

namespace EventTests.Daemon;

public class ProjectionCoordinatorBaseTests
{
    private static readonly ShardName TheShard = new("Trip", "All", 1);

    private static TestCoordinator BuildCoordinator(FakeDistributor distributor, FakeDaemon daemon,
        TimeSpan? leadershipPollingTime = null, ILogger? logger = null)
    {
        return new TestCoordinator(
            distributor,
            daemon,
            leadershipPollingTime: leadershipPollingTime ?? TimeSpan.FromSeconds(30),
            agentPauseTime: TimeSpan.FromSeconds(30),
            healthCheckPollingTime: TimeSpan.FromSeconds(30),
            logger: logger);
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

    // ---- jasperfx#489: per-tenant reconciliation in the start-agents pass ----
    //
    // Under per-tenant agent distribution the distributor re-expands each set's Names
    // from the current tenant list on every leadership cycle. The coordinator's owned
    // pass must converge the running agents onto those Names: tenants added on another
    // node get agents started (through the existing #487 tenant-bearing StartAgentAsync
    // branch), removed tenants get their agents reaped, and the store-global agent left
    // over from a pre-expansion cycle is retired once per-tenant names appear (same
    // shape as Wolverine's #3328 stale-agent retirement).

    [Fact]
    public async Task starts_an_agent_for_a_tenant_added_between_leadership_cycles()
    {
        var t1 = TheShard.ForTenant("t1");
        var daemon = new FakeDaemon();
        var distributor = new FakeDistributor([new FakeProjectionSet("db1", [t1], 100)]) { LockHeld = true };

        var coordinator = BuildCoordinator(distributor, daemon, TimeSpan.FromMilliseconds(25));
        await coordinator.StartAsync(CancellationToken.None);
        await WaitFor(() => daemon.Started.Contains("Trip:All:t1"));

        // A tenant registered on another node shows up in the next cycle's expansion.
        distributor.Sets = [new FakeProjectionSet("db1", [t1, TheShard.ForTenant("t2")], 100)];
        await WaitFor(() => daemon.Started.Contains("Trip:All:t2"));

        await coordinator.StopAsync(CancellationToken.None);

        daemon.Started.ShouldContain("Trip:All:t2");
        // The tenant that was already running is untouched.
        daemon.Stopped.ShouldNotContain("Trip:All:t1");
    }

    [Fact]
    public async Task reaps_the_agent_for_a_tenant_removed_between_leadership_cycles()
    {
        var t1 = TheShard.ForTenant("t1");
        var t2 = TheShard.ForTenant("t2");
        var daemon = new FakeDaemon();
        var distributor = new FakeDistributor([new FakeProjectionSet("db1", [t1, t2], 100)]) { LockHeld = true };

        var coordinator = BuildCoordinator(distributor, daemon, TimeSpan.FromMilliseconds(25));
        await coordinator.StartAsync(CancellationToken.None);
        await WaitFor(() => daemon.Started.Contains("Trip:All:t1") && daemon.Started.Contains("Trip:All:t2"));

        // Tenant t2 was removed — the next expansion no longer carries its name.
        distributor.Sets = [new FakeProjectionSet("db1", [t1], 100)];
        await WaitFor(() => daemon.Stopped.Contains("Trip:All:t2"));

        await coordinator.StopAsync(CancellationToken.None);

        daemon.Stopped.ShouldContain("Trip:All:t2");
        daemon.Stopped.ShouldNotContain("Trip:All:t1");
        daemon.CurrentAgents().Select(x => x.Name.Identity).ShouldBe(["Trip:All:t1"]);
    }

    [Fact]
    public async Task retires_a_store_global_agent_when_per_tenant_names_first_appear()
    {
        var daemon = new FakeDaemon();
        // Store-global agent left over from a cycle before the store's tenant list
        // could be enumerated (the transition edge).
        daemon.SeedRunningAgent(TheShard);

        var distributor = new FakeDistributor([new FakeProjectionSet("db1", [TheShard.ForTenant("t1")], 100)])
        {
            LockHeld = true
        };

        var coordinator = BuildCoordinator(distributor, daemon);
        await coordinator.StartAsync(CancellationToken.None);
        await WaitFor(() => daemon.Started.Contains("Trip:All:t1"));

        await coordinator.StopAsync(CancellationToken.None);

        daemon.Stopped.ShouldContain("Trip:All");
        // The stale store-global agent stops BEFORE its per-tenant replacement starts,
        // so the two never process the same events concurrently.
        daemon.Log.IndexOf("stop:Trip:All").ShouldBeLessThan(daemon.Log.IndexOf("start:Trip:All:t1"));
    }

    [Fact]
    public async Task reaping_matches_on_the_tenant_neutral_base_and_never_touches_other_projections()
    {
        var daemon = new FakeDaemon();
        // Agents for a DIFFERENT projection on the same daemon — one store-global, one
        // tenant-bearing — must never be reaped by the Trip set's reconciliation.
        var day = new ShardName("Day", "All", 1);
        daemon.SeedRunningAgent(day);
        daemon.SeedRunningAgent(day.ForTenant("t9"));

        var distributor = new FakeDistributor([new FakeProjectionSet("db1", [TheShard.ForTenant("t1")], 100)])
        {
            LockHeld = true
        };

        var coordinator = BuildCoordinator(distributor, daemon);
        await coordinator.StartAsync(CancellationToken.None);
        await WaitFor(() => daemon.Started.Contains("Trip:All:t1"));

        await coordinator.StopAsync(CancellationToken.None);

        daemon.Stopped.ShouldBeEmpty();
    }

    [Fact]
    public async Task reconciliation_is_inert_for_a_non_partitioned_set()
    {
        // A store that does not distribute agents per tenant keeps store-global Names,
        // so the reconciliation pass finds nothing to reap across leadership cycles.
        var daemon = new FakeDaemon();
        var distributor = new FakeDistributor([new FakeProjectionSet("db1", [TheShard], 100)]) { LockHeld = true };

        var coordinator = BuildCoordinator(distributor, daemon, TimeSpan.FromMilliseconds(25));
        await coordinator.StartAsync(CancellationToken.None);
        await WaitFor(() => daemon.Started.Contains(TheShard.Identity));

        // Let several more leadership cycles run.
        await Task.Delay(250);

        await coordinator.StopAsync(CancellationToken.None);

        daemon.Stopped.ShouldBeEmpty();
        // And the running agent was never churned — exactly one start.
        daemon.Started.Count(x => x == TheShard.Identity).ShouldBe(1);
    }

    // ---- jasperfx#499: shutdown drain race in the lock-attempt catch ----
    //
    // The per-set error handler used to treat ANY exception from TryAttainLockAsync as a
    // transient "will retry later" and loop again after a Task.Delay. During host shutdown
    // that amplified one racing OpenAsync against a disposing NpgsqlDataSource into a burst
    // of ObjectDisposedException aborts (marten#4874 case B). The loop must now terminate.

    [Fact]
    public async Task terminates_the_loop_when_the_data_source_is_disposed_mid_lock_attempt()
    {
        var set = new FakeProjectionSet("db1", [TheShard], 100);
        var daemon = new FakeDaemon();
        // Every lock attempt faults the way a disposed pool does. A re-polling loop would
        // keep incrementing AttainAttempts forever; the fix returns out of executeAsync.
        var distributor = new FakeDistributor([set])
        {
            AttainFault = _ => new ObjectDisposedException("Npgsql.PoolingDataSource")
        };

        var coordinator = BuildCoordinator(distributor, daemon, TimeSpan.FromMilliseconds(10));
        await coordinator.StartAsync(CancellationToken.None);

        await WaitFor(() => distributor.AttainAttempts >= 1);

        // Give a re-polling loop ample opportunity to fire again (poll time is 10ms).
        await Task.Delay(250);
        distributor.AttainAttempts.ShouldBe(1);

        // No agents were started against the dead data source.
        daemon.Started.ShouldBeEmpty();

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task terminates_on_a_wrapped_cancellation_during_a_lock_attempt_without_logging_a_lock_error()
    {
        var set = new FakeProjectionSet("db1", [TheShard], 100);
        var daemon = new FakeDaemon();
        var logger = new CapturingLogger();

        // The lock attempt is in-flight (blocked on the loop's own token) when the coordinator
        // is stopped; its cancellation surfaces WRAPPED. The loop must return on the
        // stoppingToken check rather than logging a "will retry later" lock error.
        var distributor = new FakeDistributor([set]) { BlockThenWrapCancellation = true };

        var coordinator = BuildCoordinator(distributor, daemon, TimeSpan.FromMilliseconds(10), logger);
        await coordinator.StartAsync(CancellationToken.None);

        // Wait until the loop is parked inside the in-flight lock attempt.
        await WaitFor(() => distributor.AttainAttempts >= 1);

        // Stopping cancels the loop token, faulting the in-flight attempt with the wrapped
        // OperationCanceledException. StopAsync completes cleanly (no drain deadlock) once the
        // loop returns.
        await coordinator.StopAsync(CancellationToken.None);

        distributor.AttainAttempts.ShouldBe(1);
        daemon.Started.ShouldBeEmpty();
        logger.Errors.ShouldNotContain(x => x.Contains("Error trying to attain a lock"));
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

    // #352: a coordinator can be legitimately constructed but never started when the async
    // daemon is Disabled — the store's BuildDistributor returns null because there is nothing
    // to coordinate. The ctor must not reject that; the lifecycle methods must no-op cleanly.
    [Fact]
    public void can_be_constructed_with_a_null_distributor()
    {
        var coordinator = new TestCoordinator(
            distributor: null,
            new FakeDaemon(),
            leadershipPollingTime: TimeSpan.FromSeconds(30),
            agentPauseTime: TimeSpan.FromSeconds(30),
            healthCheckPollingTime: TimeSpan.FromSeconds(30));

        coordinator.Distributor.ShouldBeNull();
    }

    [Fact]
    public async Task start_and_stop_no_op_when_the_distributor_is_null()
    {
        var daemon = new FakeDaemon();
        var coordinator = new TestCoordinator(
            distributor: null,
            daemon,
            leadershipPollingTime: TimeSpan.FromSeconds(30),
            agentPauseTime: TimeSpan.FromSeconds(30),
            healthCheckPollingTime: TimeSpan.FromSeconds(30));

        // No runner spins up, and neither lifecycle call throws on the absent distributor.
        await Should.NotThrowAsync(() => coordinator.StartAsync(CancellationToken.None));
        await Should.NotThrowAsync(() => coordinator.StopAsync(CancellationToken.None));

        // The loop never ran, so no agents were touched.
        daemon.Started.ShouldBeEmpty();
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

    // Minimal ILogger that records the formatted message of every Error-level entry so a test
    // can assert whether the loop logged a lock error (proving which catch branch fired).
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                lock (Errors)
                {
                    Errors.Add(formatter(state, exception));
                }
            }
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class TestCoordinator : ProjectionCoordinatorBase
    {
        private readonly FakeDaemon _daemon;

        public TestCoordinator(IProjectionDistributor? distributor, FakeDaemon daemon,
            TimeSpan leadershipPollingTime, TimeSpan agentPauseTime, TimeSpan healthCheckPollingTime,
            ILogger? logger = null)
            : base(distributor, logger ?? NullLogger.Instance, ResiliencePipeline.Empty, TimeProvider.System,
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
        // Mutable so jasperfx#489 reconciliation tests can change the distribution
        // between leadership cycles, the way a real distributor re-expands tenant
        // lists on every BuildDistributionAsync call.
        private volatile IReadOnlyList<IProjectionSet> _sets = sets;

        public IReadOnlyList<IProjectionSet> Sets
        {
            set => _sets = value;
        }

        public bool LockHeld { get; init; }
        public bool CanAttain { get; init; } = true;

        // jasperfx#499: lets a test model the shutdown drain race where TryAttainLockAsync
        // faults because the underlying data source is being disposed. Returns the exception
        // to throw for a given attempt (null = attain normally); the counter records how many
        // attempts the loop made, so a test can prove it did not re-poll a dead pool.
        public Func<int, Exception?>? AttainFault { get; init; }
        public int AttainAttempts;

        // jasperfx#499: models a lock attempt that is in-flight when the loop is cancelled and
        // whose cancellation surfaces WRAPPED (not a bare OperationCanceledException) — the case
        // the inner catch must terminate on rather than logging as a lock error and re-polling.
        public bool BlockThenWrapCancellation { get; init; }

        public List<IProjectionSet> ReleasedLocks { get; } = [];
        public int ReleaseAllLocksCount { get; private set; }

        public ValueTask<IReadOnlyList<IProjectionSet>> BuildDistributionAsync() => new(_sets);
        public Task RandomWait(CancellationToken token) => Task.CompletedTask;
        public bool HasLock(IProjectionSet set) => LockHeld;

        public async Task<bool> TryAttainLockAsync(IProjectionSet set, CancellationToken token)
        {
            var attempt = Interlocked.Increment(ref AttainAttempts);

            if (BlockThenWrapCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    // e.g. Medallion's TryAcquireAsync surfacing a cancelled OpenAsync wrapped.
                    throw new InvalidOperationException("wrapped shutdown cancellation", oce);
                }
            }

            var fault = AttainFault?.Invoke(attempt);
            if (fault != null)
            {
                throw fault;
            }

            return CanAttain;
        }

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

        // Interleaved start:/stop: record so tests can assert ordering — e.g. the
        // jasperfx#489 transition edge where the stale store-global agent must stop
        // BEFORE its per-tenant replacements start.
        public List<string> Log { get; } = [];

        public int DisposeCount { get; private set; }
        public int StopAllCount { get; private set; }

        private readonly List<ISubscriptionAgent> _agents = [];
        private bool _failed;

        // Pre-load a running agent, e.g. a store-global agent left over from a
        // pre-expansion leadership cycle.
        public void SeedRunningAgent(ShardName name) => addAgent(name);

        private void addAgent(ShardName name)
        {
            lock (_agents)
            {
                _agents.RemoveAll(x => x.Name.Identity == name.Identity);
                var agent = Substitute.For<ISubscriptionAgent>();
                agent.Name.Returns(name);
                agent.Status.Returns(AgentStatus.Running);
                _agents.Add(agent);
            }
        }

        public Task StartAgentAsync(string shardName, CancellationToken token)
        {
            if (ThrowOnStart)
            {
                _failed = true;
                throw new InvalidOperationException("boom");
            }

            lock (Log)
            {
                Started.Add(shardName);
                Log.Add($"start:{shardName}");
            }

            ShardName.TryParse(shardName, out var parsed);
            addAgent(parsed ?? new ShardName(shardName));
            return Task.CompletedTask;
        }

        public Task StopAgentAsync(string shardName, Exception? ex = null)
        {
            lock (Log)
            {
                Stopped.Add(shardName);
                Log.Add($"stop:{shardName}");
            }

            lock (_agents)
            {
                _agents.RemoveAll(x => x.Name.Identity == shardName);
            }

            return Task.CompletedTask;
        }

        public AgentStatus StatusFor(string shardName)
        {
            if (_failed) return StatusAfterFailure;
            return Status;
        }

        public IReadOnlyList<ISubscriptionAgent> CurrentAgents()
        {
            lock (_agents)
            {
                return _agents.ToArray();
            }
        }

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
        public DateTimeOffset? HighWaterLastPolledAt => throw new NotSupportedException();
        public bool IsHighWaterStale => throw new NotSupportedException();
        public Task RestartHighWaterAgentAsync(CancellationToken token) => throw new NotSupportedException();
        public Task WaitForShardToBeRunning(string shardName, TimeSpan timeout) => throw new NotSupportedException();
        public Task RewindSubscriptionAsync(string subscriptionName, CancellationToken token, long? sequenceFloor = 0, DateTimeOffset? timestamp = null) => throw new NotSupportedException();
    }
}
