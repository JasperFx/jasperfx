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

// DaemonSettings.SerializeTenantProjectionBatches: under per-tenant agents, every tenant of a projection builds
// and commits its batches through one single-threaded queue per store on the node, so the node holds one built
// batch of the projection instead of one per running tenant agent.
public class TenantBatchQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task runs_one_batch_at_a_time_in_the_order_they_arrived()
    {
        var queue = new TenantBatchQueue("Trip:All");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var running = 0;
        var maxRunning = 0;

        var runs = Enumerable.Range(0, 5).Select(i => queue.RunAsync(async () =>
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            lock (order) order.Add(i);

            if (i == 0) await first.Task;
            await Task.Yield();

            Interlocked.Decrement(ref running);
        }, CancellationToken.None)).ToArray();

        await waitFor(() => queue.Waiting == 4 && Volatile.Read(ref running) == 1);
        lock (order) order.ShouldBe([0]);

        first.SetResult();
        (await Task.WhenAll(runs).WaitAsync(Timeout)).ShouldAllBe(x => x);

        order.ShouldBe([0, 1, 2, 3, 4]);
        maxRunning.ShouldBe(1);
        queue.Waiting.ShouldBe(0);
    }

    [Fact]
    public async Task a_batch_abandoned_while_waiting_never_runs_and_the_queue_moves_on()
    {
        var queue = new TenantBatchQueue("Trip:All");
        var holder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = queue.RunAsync(() => holder.Task, CancellationToken.None);

        using var abandon = new CancellationTokenSource();
        var abandonedRan = false;
        var abandoned = queue.RunAsync(() =>
        {
            abandonedRan = true;
            return Task.CompletedTask;
        }, abandon.Token);

        var nextRan = false;
        var next = queue.RunAsync(() =>
        {
            nextRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        await abandon.CancelAsync();

        // Returns without waiting for the batch ahead of it
        (await abandoned.WaitAsync(Timeout)).ShouldBeFalse();
        holding.IsCompleted.ShouldBeFalse();

        holder.SetResult();
        (await holding.WaitAsync(Timeout)).ShouldBeTrue();
        (await next.WaitAsync(Timeout)).ShouldBeTrue();

        abandonedRan.ShouldBeFalse();
        nextRan.ShouldBeTrue();
        queue.Waiting.ShouldBe(0);
    }

    [Fact]
    public async Task a_batch_that_already_started_runs_to_the_end_when_abandoned()
    {
        var queue = new TenantBatchQueue("Trip:All");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abandon = new CancellationTokenSource();

        var finished = false;
        var run = queue.RunAsync(async () =>
        {
            started.SetResult();
            await release.Task;
            finished = true;
        }, abandon.Token);

        await started.Task.WaitAsync(Timeout);
        await abandon.CancelAsync();
        release.SetResult();

        (await run.WaitAsync(Timeout)).ShouldBeTrue();
        finished.ShouldBeTrue();
    }

    [Fact]
    public async Task a_failure_goes_back_to_its_caller_and_the_queue_keeps_running()
    {
        var queue = new TenantBatchQueue("Trip:All");

        await Should.ThrowAsync<DivideByZeroException>(() =>
            queue.RunAsync(() => throw new DivideByZeroException(), CancellationToken.None));

        (await queue.RunAsync(() => Task.CompletedTask, CancellationToken.None).WaitAsync(Timeout)).ShouldBeTrue();
    }

    [Fact]
    public void the_daemons_of_a_store_share_one_queue_per_projection_for_tenant_shards_only()
    {
        var projections = new FakeProjectionGraph { SerializeTenantProjectionBatches = true };
        var db1 = buildDaemon(projections, "db1");
        var db2 = buildDaemon(projections, "db2");

        var trip = db1.TenantBatchQueueFor(new ShardName("Trip", "All", 1, "t1")).ShouldNotBeNull();
        trip.ProjectionIdentity.ShouldBe("Trip:All");

        // Another tenant, on another database of the same store
        db2.TenantBatchQueueFor(new ShardName("Trip", "All", 1, "t2")).ShouldBeSameAs(trip);

        db1.TenantBatchQueueFor(new ShardName("Day", "All", 1, "t1")).ShouldNotBeSameAs(trip);
        db1.TenantBatchQueueFor(new ShardName("Trip", "All", 2, "t1")).ShouldNotBeSameAs(trip);

        // A store-global shard already builds one batch at a time
        db1.TenantBatchQueueFor(new ShardName("Trip")).ShouldBeNull();

        db1.Dispose();
        db2.Dispose();
    }

    [Fact]
    public void off_by_default()
    {
        new DaemonSettings().SerializeTenantProjectionBatches.ShouldBeFalse();

        var daemon = buildDaemon(new FakeProjectionGraph(), "db1");
        daemon.TenantBatchQueueFor(new ShardName("Trip", "All", 1, "t1")).ShouldBeNull();
        daemon.Dispose();
    }

    [Fact]
    public async Task unqueued_tenant_executions_build_their_batches_at_the_same_time()
    {
        var harness = new ExecutionHarness(queue: null);

        var running = harness.ProcessOneRangeEach(4);
        await harness.WaitForBatchesInWindow(4);

        harness.ReleaseWrites();
        await running.WaitAsync(Timeout);

        harness.MaxInWindow.ShouldBe(4);
    }

    [Fact]
    public async Task queued_tenant_executions_build_and_commit_one_batch_at_a_time()
    {
        var queue = new TenantBatchQueue("Trip:All");
        var harness = new ExecutionHarness(queue);

        var running = harness.ProcessOneRangeEach(6);

        await harness.WaitForBatchesInWindow(1);
        await waitFor(() => queue.Waiting == 5);
        harness.Started.ShouldBe(1);

        harness.ReleaseWrites();
        await running.WaitAsync(Timeout);

        harness.Started.ShouldBe(6);
        harness.Executed.ShouldBe(6);
        harness.MaxInWindow.ShouldBe(1);
    }

    [Fact]
    public async Task a_composite_member_runs_inside_its_parents_turn_without_queueing()
    {
        var queue = new TenantBatchQueue("Trip:All");
        var holder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = queue.RunAsync(() => holder.Task, CancellationToken.None);

        var harness = new ExecutionHarness(queue);
        var execution = harness.AddExecution("t1", out var agent);
        var member = new EventRange(ShardName.Compose("member"), 0, 5, agent)
        {
            Events = new List<IEvent>(),
            ActiveBatch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>(),
            BatchBehavior = BatchBehavior.Composite
        };

        await execution.ProcessRangeAsync(member).WaitAsync(Timeout);

        queue.Waiting.ShouldBe(0);
        holding.IsCompleted.ShouldBeFalse();

        holder.SetResult();
        await holding.WaitAsync(Timeout);
    }

    [Fact]
    public async Task draining_abandons_a_page_still_waiting_for_its_turn()
    {
        var queue = new TenantBatchQueue("Trip:All");
        var holder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = queue.RunAsync(() => holder.Task, CancellationToken.None);

        var harness = new ExecutionHarness(queue);
        var execution = harness.AddExecution("t1", out var agent);

        var page = new EventPage(0);
        page.CalculateCeiling(100, 10);
        await execution.EnqueueAsync(page, agent);
        await waitFor(() => queue.Waiting == 1);

        // Doesn't wait behind the batch holding the queue
        await execution.StopAndDrainAsync(CancellationToken.None).WaitAsync(Timeout);

        holder.SetResult();
        await holding.WaitAsync(Timeout);
        await waitFor(() => queue.Waiting == 0);

        harness.Started.ShouldBe(0);
    }

    [Fact]
    public async Task queued_grouped_executions_build_and_commit_one_batch_at_a_time()
    {
        var queue = new TenantBatchQueue("Trip:All");
        var harness = new GroupedHarness(queue);

        var running = harness.ProcessOneRangeEach(5);

        await harness.WaitForBatchesInWindow(1);
        await waitFor(() => queue.Waiting == 4);
        harness.Started.ShouldBe(1);

        harness.ReleaseWrites();
        await running.WaitAsync(Timeout);

        harness.Started.ShouldBe(5);
        harness.MaxInWindow.ShouldBe(1);
    }

    private static async Task waitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.Add(Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was never met");
            await Task.Delay(10);
        }
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

    // One execution and agent per tenant, all sharing the queue. Counts the batches between the start of the
    // build and their disposal, and holds every commit until the test releases the writes.
    private abstract class WindowCounter
    {
        private readonly TaskCompletionSource _writes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TenantBatchQueue? _queue;
        private int _inWindow;
        private int _maxInWindow;
        private int _started;
        private int _executed;

        protected WindowCounter(TenantBatchQueue? queue)
        {
            _queue = queue;
        }

        public int Started => Volatile.Read(ref _started);
        public int Executed => Volatile.Read(ref _executed);
        public int MaxInWindow => Volatile.Read(ref _maxInWindow);

        public void ReleaseWrites() => _writes.TrySetResult();

        public Task WaitForBatchesInWindow(int count) => waitFor(() => Volatile.Read(ref _inWindow) >= count);

        protected ISubscriptionAgent buildAgent(string tenantId)
        {
            var agent = Substitute.For<ISubscriptionAgent>();
            agent.Name.Returns(new ShardName("Trip", "All", 1, tenantId));
            agent.TenantBatchQueue.Returns(_queue);
            return agent;
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
            });

            batch.DisposeAsync().Returns(_ =>
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref _inWindow);
                }

                return ValueTask.CompletedTask;
            });

            return batch;
        }
    }

    private sealed class ExecutionHarness : WindowCounter
    {
        private readonly IEventStore<FakeOperations, FakeSession> _store;

        public ExecutionHarness(TenantBatchQueue? queue) : base(queue)
        {
            _store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            _store.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());
            _store.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                    Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    enterWindow();
                    return new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(buildBatch());
                });
        }

        public ProjectionExecution<FakeOperations, FakeSession> AddExecution(string tenantId,
            out ISubscriptionAgent agent)
        {
            agent = buildAgent(tenantId);
            return new ProjectionExecution<FakeOperations, FakeSession>(new ShardName("Trip", "All", 1, tenantId),
                new AsyncOptions(), _store, Substitute.For<IEventDatabase>(),
                Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance);
        }

        public Task ProcessOneRangeEach(int tenants)
        {
            return Task.WhenAll(Enumerable.Range(0, tenants).Select(i =>
            {
                var execution = AddExecution($"t{i}", out var agent);
                return Task.Run(() =>
                    execution.ProcessRangeAsync(new EventRange(agent, 0, 10) { Events = new List<IEvent>() }));
            }));
        }
    }

    private sealed class GroupedHarness : WindowCounter
    {
        public GroupedHarness(TenantBatchQueue? queue) : base(queue)
        {
        }

        public Task ProcessOneRangeEach(int tenants)
        {
            return Task.WhenAll(Enumerable.Range(0, tenants).Select(i =>
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
                        return Task.FromResult<IProjectionBatch>(buildBatch());
                    });

                var execution = new GroupedProjectionExecution(new ShardName("Trip", "All", 1, $"t{i}"), runner,
                    NullLogger.Instance);
                var agent = buildAgent($"t{i}");

                return Task.Run(() =>
                    execution.ProcessRangeAsync(new EventRange(agent, 0, 10) { Events = new List<IEvent>() }));
            }));
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
