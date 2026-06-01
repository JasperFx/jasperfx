using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections.Composite;

// jasperfx#407 Phase A: orchestration-level tests for the composite single-pass rebuild loop.
// These assert the executor reads the event range ONCE (not once-per-member) and honors the
// all-or-nothing stop semantics. Document-level rebuild correctness is verified downstream in
// the Marten implementation (marten#4596), which has a concrete event store.
public class CompositeReplayExecutorTests
{
    private readonly IEventLoader theLoader = Substitute.For<IEventLoader>();
    private readonly ISubscriptionExecution theExecution = Substitute.For<ISubscriptionExecution>();
    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly ISubscriptionAgent theAgent = Substitute.For<ISubscriptionAgent>();
    private readonly AsyncOptions theOptions = new() { BatchSize = 10 };
    private readonly ShardName theShard = new("Composite", ShardName.All, 0);

    private CompositeReplayExecutor theExecutor()
        => new(theShard, theLoader, theExecution, theDatabase, theOptions, NullLogger.Instance);

    private static SubscriptionExecutionRequest rebuildRequest()
        => new(0, ShardExecutionMode.Rebuild, new ErrorHandlingOptions(), Substitute.For<IDaemonRuntime>());

    private static EventPage makePage(long floor, long ceiling, int batchSize)
    {
        var page = new EventPage(floor);
        var last = Math.Min(floor + batchSize, ceiling);
        for (var seq = floor + 1; seq <= last; seq++)
        {
            var e = Event.For(new AEvent());
            e.Sequence = seq;
            page.Add(e);
        }

        page.CalculateCeiling(batchSize, ceiling);
        return page;
    }

    [Fact]
    public async Task reads_the_event_range_exactly_once_and_drives_each_page()
    {
        theAgent.Status.Returns(AgentStatus.Running);
        theDatabase.FetchHighestEventSequenceNumber(Arg.Any<CancellationToken>()).Returns(Task.FromResult(25L));

        var loadedFloors = new List<long>();
        theLoader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var floor = ci.Arg<EventRequest>().Floor;
                loadedFloors.Add(floor);
                return Task.FromResult(makePage(floor, 25, theOptions.BatchSize));
            });

        var processed = new List<(long Floor, long Ceiling)>();
        theExecution.When(x => x.ProcessRangeAsync(Arg.Any<EventRange>()))
            .Do(ci =>
            {
                var range = ci.Arg<EventRange>();
                processed.Add((range.SequenceFloor, range.SequenceCeiling));
            });

        await theExecutor().StartAsync(rebuildRequest(), theAgent, CancellationToken.None);

        // One pass: floors 0 -> 10 -> 20, never re-reading a region
        loadedFloors.ShouldBe(new long[] { 0, 10, 20 });

        // Each page handed to the composite execution exactly once, contiguous, covering (0, 25]
        processed.ShouldBe(new[] { (0L, 10L), (10L, 20L), (20L, 25L) });
        await theExecution.Received(3).ProcessRangeAsync(Arg.Any<EventRange>());
    }

    [Fact]
    public async Task advances_progression_to_ceiling_when_store_is_empty()
    {
        theDatabase.FetchHighestEventSequenceNumber(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0L));

        await theExecutor().StartAsync(rebuildRequest(), theAgent, CancellationToken.None);

        await theAgent.Received(1).MarkSuccessAsync(0);
        await theLoader.DidNotReceive().LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>());
        await theExecution.DidNotReceive().ProcessRangeAsync(Arg.Any<EventRange>());
    }

    [Fact]
    public async Task advances_to_ceiling_when_no_events_match_below_high_water()
    {
        theAgent.Status.Returns(AgentStatus.Running);
        theDatabase.FetchHighestEventSequenceNumber(Arg.Any<CancellationToken>()).Returns(Task.FromResult(25L));
        theLoader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EventPage(0))); // empty page

        await theExecutor().StartAsync(rebuildRequest(), theAgent, CancellationToken.None);

        await theLoader.Received(1).LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>());
        await theExecution.DidNotReceive().ProcessRangeAsync(Arg.Any<EventRange>());
        await theAgent.Received(1).MarkSuccessAsync(25);
    }

    [Fact]
    public async Task stops_the_single_pass_without_advancing_when_a_member_fails()
    {
        // The composite execution swallows a member failure and pauses the agent. The executor must
        // detect the non-Running status and stop instead of re-reading / advancing further.
        var status = AgentStatus.Running;
        theAgent.Status.Returns(_ => status);
        theDatabase.FetchHighestEventSequenceNumber(Arg.Any<CancellationToken>()).Returns(Task.FromResult(25L));
        theLoader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(makePage(ci.Arg<EventRequest>().Floor, 25, theOptions.BatchSize)));

        theExecution.When(x => x.ProcessRangeAsync(Arg.Any<EventRange>()))
            .Do(_ => status = AgentStatus.Paused);

        await theExecutor().StartAsync(rebuildRequest(), theAgent, CancellationToken.None);

        // Only the first page was read and processed; the pass stopped on failure
        await theLoader.Received(1).LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>());
        await theExecution.Received(1).ProcessRangeAsync(Arg.Any<EventRange>());
    }

    [Fact]
    public async Task requires_the_controller_to_be_a_subscription_agent()
    {
        var bareController = Substitute.For<ISubscriptionController>();

        await Should.ThrowAsync<ArgumentException>(() =>
            theExecutor().StartAsync(rebuildRequest(), bareController, CancellationToken.None));
    }
}
