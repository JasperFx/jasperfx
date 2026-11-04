using System.Diagnostics.Metrics;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// DaemonSettings.MaxConcurrentBatchesInFlightPerNode bounds how many projection batches are between "the
// build started" and "the batch was disposed" at once, across every agent and every database of a store.
// MaxConcurrentBatchWritesPerDatabase cannot do that: it is taken after the batch is built, so each agent
// can hold a fully built batch while it waits for a write slot.
public class BatchesInFlightPerNodeTests
{
    [Fact]
    public void the_default_is_unbounded()
    {
        var settings = new DaemonSettings();
        settings.MaxConcurrentBatchesInFlightPerNode.ShouldBe(0);
        settings.BatchesInFlightThrottle.ShouldBeNull();

        (new NulloDaemonRuntime() as IDaemonRuntime).BatchesInFlightThrottle.ShouldBeNull();
    }

    [Fact]
    public void a_positive_setting_builds_a_gate_of_that_size_and_a_nonpositive_one_removes_it()
    {
        var settings = new DaemonSettings { MaxConcurrentBatchesInFlightPerNode = 3 };
        settings.BatchesInFlightThrottle.ShouldNotBeNull().CurrentCount.ShouldBe(3);

        settings.MaxConcurrentBatchesInFlightPerNode = 0;
        settings.BatchesInFlightThrottle.ShouldBeNull();
    }

    [Fact]
    public void every_daemon_of_a_store_shares_one_gate()
    {
        // A store runs one daemon per database. A gate per daemon would multiply the bound by the database
        // count, which is exactly what the per-database throttles already do.
        var projections = new FakeProjectionGraph { MaxConcurrentBatchesInFlightPerNode = 2 };

        var first = buildDaemon(projections, "db1");
        var second = buildDaemon(projections, "db2");

        first.BatchesInFlightThrottle.ShouldNotBeNull();
        first.BatchesInFlightThrottle.ShouldBeSameAs(second.BatchesInFlightThrottle);
        first.BatchesInFlightThrottle.ShouldBeSameAs(projections.BatchesInFlightThrottle);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task unbounded_every_range_builds_its_batch_at_once()
    {
        var harness = new ExecutionHarness(gate: null);

        var running = harness.ProcessRanges(6);

        await harness.WaitForBatchesInWindow(6);
        harness.Started.ShouldBe(6);

        harness.ReleaseWrites();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        harness.MaxInWindow.ShouldBe(6);
    }

    [Fact]
    public async Task with_a_bound_no_more_than_that_many_batches_are_built_and_unwritten_at_once()
    {
        var gate = new SemaphoreSlim(2);
        var harness = new ExecutionHarness(gate);

        var running = harness.ProcessRanges(6);

        await harness.WaitForBatchesInWindow(2);

        // The other four are waiting BEFORE their batch is started, not holding a built one
        await Task.Delay(100);
        var startedWhileWritesWereHeld = harness.Started;
        harness.ReleaseWrites();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        startedWhileWritesWereHeld.ShouldBe(2);

        harness.Started.ShouldBe(6);
        harness.MaxInWindow.ShouldBe(2);
        harness.Executed.ShouldBe(6);
        gate.CurrentCount.ShouldBe(2);
    }

    [Fact]
    public async Task the_slot_is_released_when_building_the_batch_fails()
    {
        var gate = new SemaphoreSlim(1);
        var harness = new ExecutionHarness(gate);
        harness.Projection.ApplyAsync(Arg.Any<FakeOperations>(), Arg.Any<IReadOnlyList<IEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new DivideByZeroException());

        await harness.ProcessRangeWithEvents();

        gate.CurrentCount.ShouldBe(1);
        harness.Disposed.ShouldBe(1);
    }

    [Fact]
    public async Task the_slot_is_released_when_executing_the_batch_fails()
    {
        var gate = new SemaphoreSlim(1);
        var harness = new ExecutionHarness(gate) { ExecuteFailure = new DivideByZeroException() };
        harness.ReleaseWrites();

        await harness.ProcessRanges(3);

        gate.CurrentCount.ShouldBe(1);
        harness.Started.ShouldBe(3);
        harness.Disposed.ShouldBe(3);
    }

    [Fact]
    public async Task a_shard_stopped_while_waiting_for_a_slot_neither_builds_nor_releases_one()
    {
        var gate = new SemaphoreSlim(1);
        await gate.WaitAsync(); // another batch on the node holds the only slot

        var harness = new ExecutionHarness(gate);
        var waiting = harness.ProcessRanges(1);

        await Task.Delay(50);
        waiting.IsCompleted.ShouldBeFalse();

        await harness.Execution.HardStopAsync();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Started.ShouldBe(0);
        gate.CurrentCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_composite_member_does_not_take_a_slot_of_its_own()
    {
        // The composite holds the slot for the batch its members write into. A member that waited for a
        // second slot would deadlock a bound of one.
        var gate = new SemaphoreSlim(1);
        await gate.WaitAsync();

        var harness = new ExecutionHarness(gate);
        var range = new EventRange(ShardName.Compose("member"), 0, 5, harness.Agent)
        {
            Events = new List<IEvent>(),
            ActiveBatch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>(),
            BatchBehavior = BatchBehavior.Composite
        };

        await harness.Execution.ProcessRangeAsync(range).WaitAsync(TimeSpan.FromSeconds(5));

        gate.CurrentCount.ShouldBe(0);
    }

    [Fact]
    public async Task grouped_projections_are_bounded_from_the_start_of_the_build()
    {
        var gate = new SemaphoreSlim(2);
        var harness = new GroupedHarness(gate);

        var running = harness.ProcessRanges(5);

        await harness.WaitForBatchesInWindow(2);
        await Task.Delay(100);
        var startedWhileWritesWereHeld = harness.Started;
        harness.ReleaseWrites();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        startedWhileWritesWereHeld.ShouldBe(2);

        harness.Started.ShouldBe(5);
        harness.MaxInWindow.ShouldBe(2);
        gate.CurrentCount.ShouldBe(2);
    }

    [Fact]
    public async Task grouped_projections_release_the_slot_when_the_build_fails()
    {
        var gate = new SemaphoreSlim(1);
        var harness = new GroupedHarness(gate) { BuildFailure = new DivideByZeroException() };

        await harness.ProcessRanges(3);

        gate.CurrentCount.ShouldBe(1);
        harness.Started.ShouldBe(3);
    }

    private static JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> buildDaemon(
        FakeProjectionGraph projections, string databaseId)
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.Meter.Returns(new Meter("tests"));
        store.TimeProvider.Returns(TimeProvider.System);
        store.AutoCreateSchemaObjects.Returns(AutoCreate.None);

        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns(databaseId);
        database.DatabaseUri.Returns(new Uri($"fake://{databaseId}"));
        database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

        return new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
            store, database, new NulloLogger(), Substitute.For<IHighWaterDetector>(), projections);
    }

    // Counts the batches between StartProjectionBatchAsync (the build starts) and DisposeAsync, and holds
    // every ExecuteAsync until the test releases the writes.
    private abstract class WindowCounter
    {
        private readonly TaskCompletionSource _writes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inWindow;
        private int _maxInWindow;
        private int _started;
        private int _executed;
        private int _disposed;

        protected WindowCounter(SemaphoreSlim? gate)
        {
            Agent = Substitute.For<ISubscriptionAgent>();
            Agent.Name.Returns(ShardName.Compose("Trip"));
            Agent.BatchesInFlightThrottle.Returns(gate);
        }

        public ISubscriptionAgent Agent { get; }
        public Exception? ExecuteFailure { get; init; }

        public int Started => Volatile.Read(ref _started);
        public int Executed => Volatile.Read(ref _executed);
        public int Disposed => Volatile.Read(ref _disposed);
        public int MaxInWindow => Volatile.Read(ref _maxInWindow);

        public void ReleaseWrites() => _writes.TrySetResult();

        public async Task WaitForBatchesInWindow(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref _inWindow) < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Only {_inWindow} of {count} batches started building");
                }

                await Task.Delay(10);
            }
        }

        protected void enterWindow()
        {
            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _inWindow);
            int max;
            while (now > (max = Volatile.Read(ref _maxInWindow)))
            {
                Interlocked.CompareExchange(ref _maxInWindow, now, max);
            }
        }

        protected IProjectionBatch<FakeOperations, FakeSession> buildBatch()
        {
            var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
            var disposed = 0;

            batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                await _writes.Task;
                Interlocked.Increment(ref _executed);
                if (ExecuteFailure != null) throw ExecuteFailure;
            });

            batch.DisposeAsync().Returns(_ =>
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _disposed);
                    Interlocked.Decrement(ref _inWindow);
                }

                return ValueTask.CompletedTask;
            });

            return batch;
        }
    }

    private sealed class ExecutionHarness : WindowCounter
    {
        public ExecutionHarness(SemaphoreSlim? gate) : base(gate)
        {
            var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            store.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());
            store.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                    Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    enterWindow();
                    return new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(buildBatch());
                });

            Projection = Substitute.For<IJasperFxProjection<FakeOperations>>();

            Execution = new ProjectionExecution<FakeOperations, FakeSession>(ShardName.Compose("Trip"),
                new AsyncOptions(), store, Substitute.For<IEventDatabase>(), Projection, NullLogger.Instance);
        }

        public IJasperFxProjection<FakeOperations> Projection { get; }
        public ProjectionExecution<FakeOperations, FakeSession> Execution { get; }

        public Task ProcessRanges(int count)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() =>
                Execution.ProcessRangeAsync(new EventRange(Agent, i * 10, i * 10 + 10)
                {
                    Events = new List<IEvent>()
                }))));
        }

        public Task ProcessRangeWithEvents()
        {
            var @event = Substitute.For<IEvent>();
            @event.TenantId.Returns("t1");

            return Execution.ProcessRangeAsync(new EventRange(Agent, 0, 10) { Events = [@event] });
        }
    }

    private sealed class GroupedHarness : WindowCounter
    {
        public GroupedHarness(SemaphoreSlim? gate) : base(gate)
        {
            var runner = Substitute.For<IGroupedProjectionRunner>();
            runner.SliceBehavior.Returns(SliceBehavior.None);
            runner.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());
            runner.BuildBatchAsync(Arg.Any<EventRange>(), Arg.Any<ShardExecutionMode>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    // The runner loads aggregates and appends operations here, so this is where the window opens
                    enterWindow();
                    if (BuildFailure != null) throw BuildFailure;

                    return Task.FromResult<IProjectionBatch>(buildBatch());
                });

            Execution = new GroupedProjectionExecution(ShardName.Compose("Trip"), runner, NullLogger.Instance);
        }

        public Exception? BuildFailure { get; init; }
        public GroupedProjectionExecution Execution { get; }

        public Task ProcessRanges(int count)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() =>
                Execution.ProcessRangeAsync(new EventRange(Agent, i * 10, i * 10 + 10)
                {
                    Events = new List<IEvent>()
                }))));
        }
    }

    private sealed class FakeProjectionGraph :
        ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeProjectionGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
        }
    }
}
