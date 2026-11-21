using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests.Projections.Composite;

// A composite rebuild runs through the single-pass CompositeReplayExecutor, which reports a failed page by pausing or
// stopping the agent rather than throwing. ReplayAsync has to turn that into a thrown failure, the way the paged
// rebuild already does (ReplayFaultPropagationTests), or RebuildProjectionAsync reports a paused shard as rebuilt.
public class CompositeRebuildFaultPropagationTests
{
    private const long Ceiling = 10;

    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly IEventLoader theLoader = Substitute.For<IEventLoader>();
    private readonly IProjectionBatch<FakeOperations, FakeSession> theBatch =
        Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
    private readonly ISubscriptionExecution theMember = Substitute.For<ISubscriptionExecution>();
    private readonly ShardName theShardName = new("Composite", ShardName.All, 1);
    private readonly SubscriptionAgent theAgent;

    public CompositeRebuildFaultPropagationTests()
    {
        theBatch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        theStore.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(theBatch));
        theStore.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());
        theStore.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<EventFilterable>(), Arg.Any<AsyncOptions>(), Arg.Any<ShardName>())
            .Returns(theLoader);

        theLoader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => pageOf(call.Arg<EventRequest>()));

        theMember.ShardName.Returns(new ShardName("Member", ShardName.All, 1));
        theMember.CompactCachesAsync().Returns(Task.CompletedTask);
        theMember.ProcessRangeAsync(Arg.Any<EventRange>()).Returns(Task.CompletedTask);

        // Five events to a page, so the ten events make two pages
        var execution = new CompositeExecution<FakeOperations, FakeSession>(theShardName,
            new AsyncOptions { BatchSize = 5 }, theStore, theDatabase, Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance,
            [new ExecutionStage([theMember])]);

        theAgent = new SubscriptionAgent(theShardName, new AsyncOptions(), TimeProvider.System,
            theLoader, execution, new ShardStateTracker(NullLogger.Instance), Substitute.For<ISubscriptionMetrics>(),
            NullLogger.Instance);
    }

    private Task replayAsync()
    {
        var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Rebuild, new ErrorHandlingOptions(),
            new NulloDaemonRuntime()) { StartingHighWater = Ceiling };

        return theAgent.ReplayAsync(request, Ceiling, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task a_composite_rebuild_that_completes_returns()
    {
        await replayAsync();

        // Two pages of five, each committed as one batch
        await theBatch.Received(2).ExecuteAsync(Arg.Any<CancellationToken>());
        theAgent.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task a_commit_that_fails_on_a_leftover_member_progression_row_fails_the_rebuild()
    {
        // polecat#1118: a member's progression row survived the teardown, so the first commit fails the floor check
        var leftover = new ProgressionProgressOutOfOrderException("Member:All", 0, 5);
        theBatch.ExecuteAsync(Arg.Any<CancellationToken>()).ThrowsAsync(leftover);

        var thrown = await Should.ThrowAsync<ProgressionProgressOutOfOrderException>(replayAsync());
        thrown.ShouldBeSameAs(leftover);

        theAgent.Status.ShouldBe(AgentStatus.Stopped);
        theAgent.Failure.ShouldNotBeNull().Category.ShouldBe(ShardFailureCategory.ProgressionOutOfOrder);
    }

    [Fact]
    public async Task a_member_failure_that_pauses_the_shard_fails_the_rebuild()
    {
        var failure = new InvalidOperationException("a bug in the member projection");
        theMember.ProcessRangeAsync(Arg.Any<EventRange>()).ThrowsAsync(failure);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(replayAsync());
        thrown.ShouldBeSameAs(failure);

        theAgent.Status.ShouldBe(AgentStatus.Paused);
        theAgent.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_member_that_reaches_the_parking_cap_fails_the_rebuild()
    {
        var capReached = new ParkedLimitReachedException(new ShardName("Member", ShardName.All, 1),
            "Member:All has parked its limit of identities", null);
        theMember.ProcessRangeAsync(Arg.Any<EventRange>()).ThrowsAsync(capReached);

        var thrown = await Should.ThrowAsync<ParkedLimitReachedException>(replayAsync());
        thrown.ShouldBeSameAs(capReached);

        theAgent.Status.ShouldBe(AgentStatus.Paused);
        theAgent.Failure.ShouldNotBeNull().Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);
    }

    [Fact]
    public async Task a_failure_on_a_later_page_fails_the_rebuild()
    {
        var failure = new InvalidOperationException("a bug in the member projection");
        theMember.ProcessRangeAsync(Arg.Is<EventRange>(x => x.SequenceFloor > 0)).ThrowsAsync(failure);

        (await Should.ThrowAsync<InvalidOperationException>(replayAsync())).ShouldBeSameAs(failure);

        theAgent.Status.ShouldBe(AgentStatus.Paused);
    }

    [Fact]
    public async Task a_single_pass_that_runs_out_of_time_between_pages_fails_the_rebuild()
    {
        // The first page commits, then outlives the rebuild's timeout, so the pass sees the timeout between pages
        theMember.ProcessRangeAsync(Arg.Any<EventRange>()).Returns(async _ => await Task.Delay(300));

        var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Rebuild, new ErrorHandlingOptions(),
            new NulloDaemonRuntime()) { StartingHighWater = Ceiling };

        await Should.ThrowAsync<OperationCanceledException>(
            theAgent.ReplayAsync(request, Ceiling, TimeSpan.FromMilliseconds(100)));

        // The second page was never loaded
        await theMember.Received(1).ProcessRangeAsync(Arg.Any<EventRange>());
    }

    private static EventPage pageOf(EventRequest request)
    {
        var page = new EventPage(request.Floor);
        var sequence = request.Floor;
        var count = (int)Math.Min(request.BatchSize, request.HighWater - request.Floor);
        foreach (var @event in new string('a', count).ToLetterEventsWithWrapper())
        {
            @event.Sequence = ++sequence;
            page.Add(@event);
        }

        page.CalculateCeiling(request.BatchSize, request.HighWater);
        return page;
    }
}
