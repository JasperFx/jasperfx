using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Shouldly;

namespace StressTests;

// #1148: overlapping ProjectionCoordinatorBase lifecycle calls.
//
// The fixes for #574 and #592 made repeated calls safe one after another, but nothing serialized calls that
// overlap. RunCommand stops the host a second time when ApplicationStopping fires, so a
// WebApplicationFactory/Alba teardown has two IHost.StopAsync calls in flight and both reach the coordinator.
// stopRunnerAsync read _cancellation, awaited, then disposed it, so the second caller could call CancelAsync on
// a source the first had just disposed (ObjectDisposedException).
public class ProjectionCoordinatorOverlapTests
{
    // Bounded so a regression fails instead of hanging the run: the unserialized code could also deadlock, with
    // callers stuck behind a runner nothing would ever cancel.
    private static readonly TimeSpan OverlapTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task overlapping_stops_and_pauses_never_throw_and_tear_down_once()
    {
        const int callers = 32;

        for (var round = 0; round < 200; round++)
        {
            var daemon = Substitute.For<IProjectionDaemon>();
            var coordinator = new TestCoordinator(new FakeDistributor([]), daemon);
            await coordinator.StartAsync(CancellationToken.None);
            coordinator.ForceResolve();

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, callers)
                .Select(i => Task.Run(async () =>
                {
                    await gate.Task;
                    if (i % 2 == 0)
                    {
                        await coordinator.StopAsync(CancellationToken.None);
                    }
                    else
                    {
                        await coordinator.PauseAsync();
                    }
                }))
                .ToArray();

            gate.SetResult();
            await Should.NotThrowAsync(() => Task.WhenAll(calls).WaitAsync(OverlapTimeout));

            // The first stop to run disposed the daemon and purged the cache, so no other caller fanned out over
            // it a second time.
            daemon.Received(1).Dispose();
        }
    }

    [Fact]
    public async Task overlapping_resumes_and_stops_never_orphan_a_leadership_loop()
    {
        const int callers = 16;
        var distributors = new List<FakeDistributor>();

        for (var round = 0; round < 30; round++)
        {
            // Every poll calls TryAttainLockAsync, so AttainAttempts is a direct liveness counter for the loop.
            var distributor = new FakeDistributor([new FakeProjectionSet("db1", 100)]);
            distributors.Add(distributor);

            var daemon = Substitute.For<IProjectionDaemon>();
            daemon.CurrentAgents().Returns([]);

            var coordinator = new TestCoordinator(distributor, daemon);
            await coordinator.StartAsync(CancellationToken.None);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, callers)
                .Select(i => Task.Run(async () =>
                {
                    await gate.Task;
                    if (i % 3 == 0)
                    {
                        await coordinator.StopAsync(CancellationToken.None);
                    }
                    else
                    {
                        // Two StartAsync calls that overlap used to each build a runner, and the second overwrote
                        // the first, leaving a loop nothing would ever cancel.
                        await coordinator.ResumeAsync();
                    }
                }))
                .ToArray();

            gate.SetResult();
            await Should.NotThrowAsync(() => Task.WhenAll(calls).WaitAsync(OverlapTimeout));

            await coordinator.StopAsync(CancellationToken.None).WaitAsync(OverlapTimeout);
        }

        // Every coordinator is stopped, so no leadership loop may still be polling for locks.
        var afterStop = distributors.Select(x => x.AttainAttempts).ToArray();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        distributors.Select(x => x.AttainAttempts).ToArray().ShouldBe(afterStop);
    }

    private sealed class TestCoordinator(IProjectionDistributor distributor, IProjectionDaemon daemon)
        : ProjectionCoordinatorBase(distributor, NullLogger.Instance, ResiliencePipeline.Empty, TimeProvider.System,
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5))
    {
        private volatile bool _resolved;

        public void ForceResolve() => _resolved = true;

        protected override IProjectionDaemon ResolveDaemon(IProjectionSet set)
        {
            _resolved = true;
            return daemon;
        }

        protected override IReadOnlyList<IProjectionDaemon> ResolvedDaemons() => _resolved ? [daemon] : [];

        protected override void ClearResolvedDaemons() => _resolved = false;

        public override IProjectionDaemon DaemonForMainDatabase() => daemon;

        public override ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier) => new(daemon);

        public override ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync() => new([daemon]);
    }

    // A set with no shard names: the loop still polls TryAttainLockAsync for it every cycle, but never has an
    // agent to start or stop.
    private sealed class FakeProjectionSet(string id, int lockId) : IProjectionSet
    {
        public int LockId { get; } = lockId;
        public IProjectionDatabase Database { get; } = new FakeDatabase(id);
        public IReadOnlyList<ShardName> Names { get; } = [];
    }

    private sealed class FakeDatabase(string id) : IProjectionDatabase
    {
        public string Identifier { get; } = id;
        public Uri DatabaseUri { get; } = new($"fake://{id}");
    }

    private sealed class FakeDistributor(IReadOnlyList<IProjectionSet> sets) : IProjectionDistributor
    {
        private int _attainAttempts;

        public int AttainAttempts => Volatile.Read(ref _attainAttempts);

        public ValueTask<IReadOnlyList<IProjectionSet>> BuildDistributionAsync() => new(sets);
        public Task RandomWait(CancellationToken token) => Task.CompletedTask;
        public bool HasLock(IProjectionSet set) => false;

        public Task<bool> TryAttainLockAsync(IProjectionSet set, CancellationToken token)
        {
            Interlocked.Increment(ref _attainAttempts);
            return Task.FromResult(false);
        }

        public Task ReleaseLockAsync(IProjectionSet set) => Task.CompletedTask;
        public Task ReleaseAllLocks() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
