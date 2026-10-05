using System.Collections.Concurrent;
using System.Threading.Channels;
using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#980: a node that lost a projection shard's lock kept applying its whole queued backlog while
// another node ran the shard. Same failure #953 fixed for subscriptions, against both projection executions.
public class ProjectionExecutionStopAndDrainTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public enum ExecutionKind
    {
        Projection,
        Grouped
    }

    [Theory]
    [InlineData(ExecutionKind.Projection)]
    [InlineData(ExecutionKind.Grouped)]
    public async Task stop_and_drain_finishes_the_page_in_flight_and_skips_the_queued_ones(ExecutionKind kind)
    {
        var batches = new GatedBatches();
        await using var execution = batches.BuildExecution(kind);
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.EnqueueAsync(new EventPage(0), agent);
        await execution.EnqueueAsync(new EventPage(100), agent);
        await execution.EnqueueAsync(new EventPage(200), agent);

        var inFlight = await batches.NextStartedAsync();
        inFlight.Floor.ShouldBe(0);

        var stop = execution.StopAndDrainAsync(CancellationToken.None);
        inFlight.Release();
        await stop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        batches.Started.ShouldBe([0]);
        await agent.Received(1).MarkSuccessAsync(Arg.Any<long>());
    }

    [Theory]
    [InlineData(ExecutionKind.Projection)]
    [InlineData(ExecutionKind.Grouped)]
    public async Task stop_and_drain_cancels_the_page_in_flight_once_its_token_is_cancelled(ExecutionKind kind)
    {
        var batches = new GatedBatches();
        await using var execution = batches.BuildExecution(kind);
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.EnqueueAsync(new EventPage(0), agent);
        await execution.EnqueueAsync(new EventPage(100), agent);

        await batches.NextStartedAsync();

        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            execution.StopAndDrainAsync(drainTimeout.Token).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        await execution.StopAndDrainAsync(CancellationToken.None)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        batches.Started.ShouldBe([0]);
        await agent.DidNotReceive().MarkSuccessAsync(Arg.Any<long>());
        await agent.DidNotReceive().ReportCriticalFailureAsync(Arg.Any<Exception>());
    }

    [Theory]
    [InlineData(ExecutionKind.Projection)]
    [InlineData(ExecutionKind.Grouped)]
    public async Task a_page_that_finishes_after_its_drain_timed_out_does_not_mark_progression(ExecutionKind kind)
    {
        var batches = new GatedBatches { HonoursCancellation = false };
        await using var execution = batches.BuildExecution(kind);
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.EnqueueAsync(new EventPage(0), agent);

        var inFlight = await batches.NextStartedAsync();

        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            execution.StopAndDrainAsync(drainTimeout.Token).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        inFlight.Release();
        await execution.StopAndDrainAsync(CancellationToken.None)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await agent.DidNotReceive().MarkSuccessAsync(Arg.Any<long>());
    }

    [Theory]
    [InlineData(ExecutionKind.Projection)]
    [InlineData(ExecutionKind.Grouped)]
    public async Task a_page_enqueued_after_the_drain_started_is_ignored(ExecutionKind kind)
    {
        var batches = new GatedBatches();
        await using var execution = batches.BuildExecution(kind);
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.StopAndDrainAsync(CancellationToken.None)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await execution.EnqueueAsync(new EventPage(0), agent);

        batches.Started.ShouldBeEmpty();
    }

    // Replays and composite stages call ProcessRangeAsync directly, outside the queue, and must keep running
    [Theory]
    [InlineData(ExecutionKind.Projection)]
    [InlineData(ExecutionKind.Grouped)]
    public async Task process_range_directly_still_applies_and_marks_progression(ExecutionKind kind)
    {
        var batches = new GatedBatches { Gated = false };
        await using var execution = batches.BuildExecution(kind);
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.ProcessRangeAsync(new EventRange(agent, 0, 100) { Events = [] })
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        batches.Started.ShouldBe([0]);
        await agent.Received(1).MarkSuccessAsync(100);
    }

    private sealed class GatedBatches
    {
        private readonly ConcurrentQueue<long> _started = new();
        private readonly Channel<PageGate> _gates = Channel.CreateUnbounded<PageGate>();

        public long[] Started => _started.ToArray();

        public bool HonoursCancellation { get; init; } = true;

        public bool Gated { get; init; } = true;

        public async Task<PageGate> NextStartedAsync()
        {
            return await _gates.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }

        public ISubscriptionExecution BuildExecution(ExecutionKind kind)
        {
            var name = ShardName.Compose("Gated");

            if (kind == ExecutionKind.Projection)
            {
                var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
                store.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());
                store.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                        Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
                    .Returns(call => new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(
                        BuildBatch<IProjectionBatch<FakeOperations, FakeSession>>(call.Arg<EventRange>())));

                return new ProjectionExecution<FakeOperations, FakeSession>(name, new AsyncOptions(), store,
                    Substitute.For<IEventDatabase>(), Substitute.For<IJasperFxProjection<FakeOperations>>(),
                    NullLogger.Instance);
            }

            var runner = Substitute.For<IGroupedProjectionRunner>();
            runner.SliceBehavior.Returns(SliceBehavior.None);
            runner.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());
            runner.BuildBatchAsync(Arg.Any<EventRange>(), Arg.Any<ShardExecutionMode>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IProjectionBatch>(BuildBatch<IProjectionBatch>(call.Arg<EventRange>())));

            return new GroupedProjectionExecution(name, runner, NullLogger.Instance);
        }

        private T BuildBatch<T>(EventRange range) where T : class, IProjectionBatch
        {
            var batch = Substitute.For<T>();
            batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(call => executeAsync(range, call.Arg<CancellationToken>()));
            return batch;
        }

        private async Task executeAsync(EventRange range, CancellationToken token)
        {
            _started.Enqueue(range.SequenceFloor);

            if (!Gated) return;

            var gate = new PageGate(range.SequenceFloor);
            await _gates.Writer.WriteAsync(gate, CancellationToken.None);

            await gate.WaitAsync(HonoursCancellation ? token : CancellationToken.None);
        }
    }

    private sealed class PageGate(long floor)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Floor { get; } = floor;

        public void Release() => _released.TrySetResult();

        public Task WaitAsync(CancellationToken token) => _released.Task.WaitAsync(token);
    }
}
