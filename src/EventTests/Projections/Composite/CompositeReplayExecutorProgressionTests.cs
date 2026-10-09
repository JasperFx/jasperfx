using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections.Composite;

// A single-pass composite replay that ends on an empty page must still persist progression, or the
// first continuous page after it updates a row that is missing or behind and the shard stops.
public class CompositeReplayExecutorProgressionTests
{
    private const long Ceiling = 10;

    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly IEventLoader theLoader = Substitute.For<IEventLoader>();
    private readonly ISubscriptionAgent theAgent = Substitute.For<ISubscriptionAgent>();
    private readonly FakeProgressionTable theProgression = new();
    private readonly ShardName theShardName = new("Composite", ShardName.All, 1);
    private readonly CompositeExecution<FakeOperations, FakeSession> theExecution;

    public CompositeReplayExecutorProgressionTests()
    {
        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        batch.RecordProgress(Arg.Any<EventRange>()).Returns(call =>
        {
            theProgression.Record(call.Arg<EventRange>());
            return ValueTask.CompletedTask;
        });

        // Like Marten, starting a batch records the composite's own progress for the range
        theStore.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                theProgression.Record(call.Arg<EventRange>());
                return new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch);
            });
        theStore.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>())
            .Returns(new ErrorHandlingOptions { SkipApplyErrors = false });

        var member = Substitute.For<ISubscriptionExecution>();
        member.ShardName.Returns(new ShardName("Member", ShardName.All, 1));
        member.CompactCachesAsync().Returns(Task.CompletedTask);

        theExecution = new CompositeExecution<FakeOperations, FakeSession>(theShardName, new AsyncOptions(),
            theStore, theDatabase, Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance,
            [new ExecutionStage([member])]);

        theAgent.Name.Returns(theShardName);
        theAgent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());
        theAgent.Status.Returns(AgentStatus.Running);
    }

    [Fact]
    public async Task replay_over_only_filtered_out_events_persists_progression_at_the_ceiling()
    {
        theLoader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => pageOf(call.Arg<EventRequest>()));

        await replayAsync(batchSize: 500);

        theProgression.PositionOf("Composite:All").ShouldBe(Ceiling);
        theProgression.PositionOf("Member:All").ShouldBe(Ceiling);
        await continuousPageAfterTheReplayShouldNotFail();
    }

    [Fact]
    public async Task replay_ending_on_a_full_page_followed_by_an_empty_page_persists_progression_at_the_ceiling()
    {
        theLoader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<EventRequest>();
                return request.Floor == 0 ? pageOf(request, "ab") : pageOf(request);
            });

        await replayAsync(batchSize: 2);

        theProgression.PositionOf("Composite:All").ShouldBe(Ceiling);
        await continuousPageAfterTheReplayShouldNotFail();
    }

    private async Task replayAsync(int batchSize)
    {
        var executor = new CompositeReplayExecutor(theShardName, theLoader, theExecution, theDatabase,
            new AsyncOptions { BatchSize = batchSize }, NullLogger.Instance);

        var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.CatchUp, new ErrorHandlingOptions(),
            Substitute.For<IDaemonRuntime>()) { StartingHighWater = Ceiling };

        await executor.StartAsync(request, theAgent, CancellationToken.None);

        await theAgent.Received().MarkSuccessAsync(Ceiling);
    }

    private async Task continuousPageAfterTheReplayShouldNotFail()
    {
        theExecution.Mode = ShardExecutionMode.Continuous;
        await theExecution.ProcessRangeAsync(new EventRange(theAgent, Ceiling, Ceiling + 5) { Events = [] });

        await theAgent.DidNotReceive().ReportCriticalFailureAsync(Arg.Any<Exception>());
        theProgression.PositionOf("Composite:All").ShouldBe(Ceiling + 5);
    }

    private static EventPage pageOf(EventRequest request, string letters = "")
    {
        var page = new EventPage(request.Floor);
        long sequence = request.Floor;
        foreach (var @event in letters.ToLetterEventsWithWrapper())
        {
            @event.Sequence = ++sequence;
            page.Add(@event);
        }

        page.CalculateCeiling(request.BatchSize, request.HighWater);
        return page;
    }

    // Mirrors Marten's progression writes: a range from floor 0 inserts the row, any other range
    // updates it only where it is still at that floor.
    private class FakeProgressionTable
    {
        private readonly Dictionary<string, long> _rows = new();

        public void Record(EventRange range)
        {
            var name = range.ShardName.Identity;
            if (range.SequenceFloor == 0)
            {
                _rows.Add(name, range.SequenceCeiling);
                return;
            }

            if (!_rows.TryGetValue(name, out var current) || current != range.SequenceFloor)
            {
                throw new ProgressionProgressOutOfOrderException(name, range.SequenceFloor, range.SequenceCeiling);
            }

            _rows[name] = range.SequenceCeiling;
        }

        public long? PositionOf(string identityPrefix) =>
            _rows.Where(x => x.Key.StartsWith(identityPrefix)).Select(x => (long?)x.Value).SingleOrDefault();
    }
}
