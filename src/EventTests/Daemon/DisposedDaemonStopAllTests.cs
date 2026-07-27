using System.Diagnostics.Metrics;
using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// marten#5055: at Kubernetes pod shutdown a second Pause/Stop pass (double AddAsyncDaemon
// hosted-service registration, user pause + host stop, Wolverine quiesce + host stop) fans
// StopAllAsync out over daemons the first pass already disposed. StopAllAsync used to open with
// _semaphore.WaitAsync(_cancellation.Token), and reading .Token off the disposed source threw
// ObjectDisposedException — one "Error while trying to stop daemon agents" log per daemon, on
// every shutdown. A disposed daemon has nothing left to stop, so StopAllAsync must be a no-op.
public class DisposedDaemonStopAllTests
{
    private static JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> BuildDaemon()
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.Meter.Returns(new Meter("tests"));
        store.TimeProvider.Returns(TimeProvider.System);

        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("db1");
        database.DatabaseUri.Returns(new Uri("fake://db1"));
        database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

        return new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
            store, database, new NulloLogger(), new StubDetector(), new FakeProjectionGraph());
    }

    [Fact]
    public async Task stop_all_after_dispose_is_a_no_op_instead_of_throwing()
    {
        var daemon = BuildDaemon();
        daemon.Dispose();

        await Should.NotThrowAsync(daemon.StopAllAsync);
    }

    [Fact]
    public async Task stop_all_after_a_stop_dispose_cycle_is_still_a_no_op()
    {
        // The exact shutdown shape from the issue: the first coordinator pass stops then disposes,
        // the second pass calls StopAllAsync again on the same instance.
        var daemon = BuildDaemon();
        await daemon.StopAllAsync();
        daemon.Dispose();

        await Should.NotThrowAsync(daemon.StopAllAsync);
    }

    [Fact]
    public void dispose_is_idempotent()
    {
        var daemon = BuildDaemon();
        daemon.Dispose();

        Should.NotThrow(daemon.Dispose);
    }

    private sealed class StubDetector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics());

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
    }

    // Minimal concrete ProjectionGraph — on these paths the daemon consumes it only as DaemonSettings.
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
}
