using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

public class SubscriptionAgentBackfillTests
{
    private readonly ISubscriptionExecution theExecution = Substitute.For<ISubscriptionExecution>();
    private readonly IEventLoader theLoader = Substitute.For<IEventLoader>();
    private readonly FakeBackfill theBackfill = new();
    private readonly List<long> theLoadedFloors = new();
    private readonly ShardStateTracker theTracker = new(NullLogger.Instance);
    private readonly ConcurrentQueue<ShardState> thePublishedStates = new();
    private readonly SubscriptionAgent theAgent;

    public SubscriptionAgentBackfillTests()
    {
        theExecution.TryBuildBackfillExecutor(out Arg.Any<IBackfillExecutor?>())
            .Returns(call =>
            {
                call[0] = theBackfill;
                return true;
            });

        theLoader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<EventRequest>();
                lock (theLoadedFloors) theLoadedFloors.Add(request.Floor);

                var page = new EventPage(request.Floor) { new Event<AEvent>(new AEvent()) { Sequence = request.HighWater } };
                page.CalculateCeiling(request.BatchSize, request.HighWater);
                return page;
            });

        theAgent = new SubscriptionAgent(new ShardName("Projection1"), new AsyncOptions { BatchSize = 300 }, TimeProvider.System,
            theLoader, theExecution, theTracker, Substitute.For<ISubscriptionMetrics>(),
            NullLogger.Instance);

        theTracker.Subscribe(new Recorder(thePublishedStates));
    }

    private Task startAsync(long floor, long highWater) =>
        theAgent.StartAsync(new SubscriptionExecutionRequest(floor, ShardExecutionMode.Continuous,
            new ErrorHandlingOptions(), new NulloDaemonRuntime()) { StartingHighWater = highWater });

    private bool loaded(long floor)
    {
        lock (theLoadedFloors) return theLoadedFloors.Contains(floor);
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task the_shard_keeps_loading_while_its_backfill_catches_up()
    {
        await startAsync(10, 20);

        await theBackfill.CatchUpStarted.Task.WaitAsync(5.Seconds());
        await waitForAsync(() => loaded(10));

        theBackfill.HandedOverAt.ShouldBeNull();

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task the_hand_over_waits_until_nothing_is_in_flight_and_gets_the_committed_position()
    {
        await startAsync(10, 20);
        await waitForAsync(() => loaded(10));

        // Caught up while the page 10-20 is still in flight
        theBackfill.CatchUp.SetResult(true);
        await Task.Delay(100);
        theBackfill.HandOverStarted.Task.IsCompleted.ShouldBeFalse();

        await theAgent.MarkSuccessAsync(20);

        (await theBackfill.HandOverStarted.Task.WaitAsync(5.Seconds())).ShouldBe(20);

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task the_shard_does_not_load_during_the_hand_over_and_resumes_after_it()
    {
        await startAsync(10, 20);
        await waitForAsync(() => loaded(10));
        theBackfill.CatchUp.SetResult(true);
        await theAgent.MarkSuccessAsync(20);
        await theBackfill.HandOverStarted.Task.WaitAsync(5.Seconds());

        theAgent.MarkHighWater(30);
        await Task.Delay(100);
        loaded(20).ShouldBeFalse();

        theBackfill.HandOver.SetResult();
        await waitForAsync(() => loaded(20));

        theExecution.Received().ResetAggregateCaches();

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task a_failing_backfill_does_not_stop_the_shard()
    {
        await startAsync(10, 20);
        await waitForAsync(() => loaded(10));

        theBackfill.CatchUp.SetException(new DivideByZeroException());
        await theAgent.MarkSuccessAsync(20);

        theAgent.MarkHighWater(30);
        await waitForAsync(() => loaded(20));
        theAgent.Status.ShouldBe(AgentStatus.Running);

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task a_shard_that_starts_from_nothing_settles_its_backfills_instead_of_running_them()
    {
        await startAsync(0, 20);

        await theBackfill.Settled.Task.WaitAsync(5.Seconds());
        await waitForAsync(() => loaded(0));

        theBackfill.CatchUpStarted.Task.IsCompleted.ShouldBeFalse();

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task a_forced_catch_up_hands_the_backfill_over_at_the_shards_position_before_projecting()
    {
        theBackfill.CatchUp.SetResult(true);
        theBackfill.HandOver.SetResult();

        await theAgent.CatchUpAsync(20, new ShardState(new ShardName("Projection1"), 10), CancellationToken.None);

        theBackfill.HandedOverAt.ShouldBe(10);
        loaded(10).ShouldBeTrue();
        theExecution.Received().ResetAggregateCaches();
    }

    [Fact]
    public async Task a_forced_catch_up_of_a_shard_already_at_the_high_water_mark_still_backfills()
    {
        theBackfill.CatchUp.SetResult(true);
        theBackfill.HandOver.SetResult();

        await theAgent.CatchUpAsync(20, new ShardState(new ShardName("Projection1"), 20), CancellationToken.None);

        theBackfill.HandedOverAt.ShouldBe(20);
    }

    [Fact]
    public async Task a_forced_catch_up_from_nothing_settles_the_backfill()
    {
        await theAgent.CatchUpAsync(20, new ShardState(new ShardName("Projection1"), 0), CancellationToken.None);

        theBackfill.Settled.Task.IsCompleted.ShouldBeTrue();
        theBackfill.CatchUpStarted.Task.IsCompleted.ShouldBeFalse();
    }

    private async Task<ShardState> publishedAsync(Func<ShardState, bool> matches)
    {
        ShardState? found = null;
        await waitForAsync(() => (found = thePublishedStates.LastOrDefault(matches)) != null);
        return found!;
    }

    [Fact]
    public async Task the_backfill_pages_by_the_shards_own_batch_size()
    {
        await startAsync(10, 20);
        await theBackfill.CatchUpStarted.Task.WaitAsync(5.Seconds());

        theBackfill.Request!.BatchSize.ShouldBe(300);

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task the_backfill_progress_rides_on_the_shards_published_state()
    {
        theBackfill.Progress = new BackfillProgress("Projection1:All:year", 5, 20);
        await startAsync(10, 20);

        var state = await publishedAsync(x => x.Backfills != null);

        state.Sequence.ShouldBe(10);
        state.Mode.ShouldBe(ShardMode.continuous);
        state.Backfills.ShouldHaveSingleItem().ShouldBe(theBackfill.Progress);

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task a_failing_backfill_stays_visible_on_the_shards_state()
    {
        theBackfill.Progress = new BackfillProgress("Projection1:All:year", 5, 20);
        await startAsync(10, 20);
        await publishedAsync(x => x.Backfills != null);

        theBackfill.CatchUp.SetException(new DivideByZeroException());

        var failed = (await publishedAsync(x => x.Backfills?.Any(b => b.Failure != null) == true)).Backfills!.Single();
        failed.Failure!.ExceptionType.ShouldContain(nameof(DivideByZeroException));

        thePublishedStates.Clear();
        await theAgent.MarkSuccessAsync(20);
        (await publishedAsync(x => x.Sequence == 20)).Backfills.ShouldNotBeNull();

        await theAgent.DisposeAsync();
    }

    [Fact]
    public async Task a_handed_over_backfill_is_no_longer_published()
    {
        theBackfill.Progress = new BackfillProgress("Projection1:All:year", 5, 20);
        await startAsync(10, 20);
        await waitForAsync(() => loaded(10));
        theBackfill.CatchUp.SetResult(true);
        theBackfill.HandOver.SetResult();
        await theAgent.MarkSuccessAsync(20);
        await theBackfill.HandOverStarted.Task.WaitAsync(5.Seconds());

        theAgent.MarkHighWater(30);
        await waitForAsync(() => loaded(20));
        thePublishedStates.Clear();
        await theAgent.MarkSuccessAsync(30);

        (await publishedAsync(x => x.Sequence == 30)).Backfills.ShouldBeNull();

        await theAgent.DisposeAsync();
    }

    private sealed class Recorder(ConcurrentQueue<ShardState> states): IObserver<ShardState>
    {
        public void OnNext(ShardState value) => states.Enqueue(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed class FakeBackfill: IBackfillExecutor
    {
        public readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CatchUpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> CatchUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<long> HandOverStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource HandOver = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long? HandedOverAt { get; private set; }

        public BackfillProgress? Progress { get; set; }

        public BackfillRequest? Request { get; private set; }

        public Task SettleAsync(CancellationToken token)
        {
            Settled.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<bool> CatchUpAsync(Func<long> liveProgression, BackfillRequest request, CancellationToken token)
        {
            Request = request;
            if (Progress != null) await request.ReportAsync(Progress);
            CatchUpStarted.TrySetResult();
            return await CatchUp.Task;
        }

        public Task HandOverAsync(long ceiling, BackfillRequest request, CancellationToken token)
        {
            HandedOverAt = ceiling;
            HandOverStarted.TrySetResult(ceiling);
            return HandOver.Task;
        }
    }
}
