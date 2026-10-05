using System.Collections.Concurrent;
using System.Threading.Channels;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// A node that lost a subscription's lock kept processing its whole queued backlog while another node ran it
public class SubscriptionExecutionStopAndDrainTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task stop_and_drain_finishes_the_range_in_flight_and_skips_the_queued_ones()
    {
        var execution = new GatedSubscriptionExecution();
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.EnqueueAsync(new EventPage(0), agent);
        await execution.EnqueueAsync(new EventPage(100), agent);
        await execution.EnqueueAsync(new EventPage(200), agent);

        var inFlight = await execution.NextStartedAsync();
        inFlight.Floor.ShouldBe(0);

        var stop = execution.StopAndDrainAsync(CancellationToken.None);
        inFlight.Release();
        await stop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        execution.Started.ShouldBe([0]);
        await agent.Received(1).MarkSuccessAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task stop_and_drain_cancels_the_range_in_flight_once_its_token_is_cancelled()
    {
        var execution = new GatedSubscriptionExecution();
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.EnqueueAsync(new EventPage(0), agent);
        await execution.EnqueueAsync(new EventPage(100), agent);

        await execution.NextStartedAsync();

        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            execution.StopAndDrainAsync(drainTimeout.Token).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        await execution.StopAndDrainAsync(CancellationToken.None)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        execution.Started.ShouldBe([0]);
        await agent.DidNotReceive().MarkSuccessAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task a_range_that_finishes_after_its_drain_timed_out_does_not_mark_progression()
    {
        var execution = new GatedSubscriptionExecution { HonoursCancellation = false };
        var agent = Substitute.For<ISubscriptionAgent>();

        await execution.EnqueueAsync(new EventPage(0), agent);

        var inFlight = await execution.NextStartedAsync();

        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            execution.StopAndDrainAsync(drainTimeout.Token).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        inFlight.Release();
        await execution.StopAndDrainAsync(CancellationToken.None)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await agent.DidNotReceive().MarkSuccessAsync(Arg.Any<long>());
    }

    private sealed class GatedSubscriptionExecution() : SubscriptionExecutionBase(Substitute.For<IEventDatabase>(),
        new ShardName("Gated"), NullLogger.Instance)
    {
        private readonly ConcurrentQueue<long> _started = new();
        private readonly Channel<RangeGate> _gates = Channel.CreateUnbounded<RangeGate>();

        public long[] Started => _started.ToArray();

        public bool HonoursCancellation { get; init; } = true;

        public async Task<RangeGate> NextStartedAsync()
        {
            return await _gates.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }

        protected override async Task executeRangeAsync(IEventDatabase database, EventRange range,
            ShardExecutionMode mode, CancellationToken cancellationToken)
        {
            _started.Enqueue(range.SequenceFloor);

            var gate = new RangeGate(range.SequenceFloor);
            await _gates.Writer.WriteAsync(gate, CancellationToken.None);

            await gate.WaitAsync(HonoursCancellation ? cancellationToken : CancellationToken.None);
        }
    }

    private sealed class RangeGate(long floor)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Floor { get; } = floor;

        public void Release() => _released.TrySetResult();

        public Task WaitAsync(CancellationToken token) => _released.Task.WaitAsync(token);
    }
}
