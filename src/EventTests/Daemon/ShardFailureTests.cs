using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#565: a shard that pauses on a poison event used to tell an external supervisor nothing but
// AgentStatus.Paused. ISubscriptionAgent had no reason accessor and ShardStateTracker kept its
// current-state map private, so Wolverine's EventSubscriptionAgent (and CritterWatch behind it) could see
// that progress had flatlined but never why — and the operator response is completely different per cause.
// These tests pin the classification, the agent/tracker surface that exposes it, and the dead-letter
// correlation.
public class ShardFailureTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private static Event<AEvent> anEvent(long sequence = 42) => new(new AEvent())
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        Version = 3,
        StreamId = Guid.NewGuid(),
        TenantId = "tenant1",
        EventTypeName = "a_event"
    };

    #region classification

    [Fact]
    public void classifies_an_apply_event_exception_and_names_the_event()
    {
        var @event = anEvent();
        var failure = ShardFailure.For(new ApplyEventException(@event, new DivideByZeroException("boom")), Now);

        failure.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        failure.OccurredAt.ShouldBe(Now);

        // The outermost type is what the daemon caught; the root is the one an operator greps for, and is
        // the same choice DeadLetterEvent.ExceptionType makes.
        failure.ExceptionType.ShouldBe(typeof(ApplyEventException).FullNameInCode());
        failure.RootExceptionType.ShouldBe(typeof(DivideByZeroException).FullNameInCode());

        failure.Event.ShouldNotBeNull();
        failure.Event.Sequence.ShouldBe(42);
        failure.Event.EventId.ShouldBe(@event.Id);
        failure.Event.EventTypeName.ShouldBe("a_event");
        failure.Event.StreamId.ShouldBe(@event.StreamId);
        failure.Event.StreamKey.ShouldBeNull(); // a Guid-identified stream has no key
        failure.Event.TenantId.ShouldBe("tenant1");
        failure.Event.Version.ShouldBe(3);
    }

    [Fact]
    public void normalizes_the_unused_half_of_the_stream_identity()
    {
        // A string-keyed stream reports StreamId as Guid.Empty. Rendering that to an operator as
        // "stream 00000000-0000-0000-0000-000000000000" is worse than saying nothing.
        var @event = anEvent();
        @event.StreamId = Guid.Empty;
        @event.StreamKey = "trip-1";

        var failure = ShardFailure.For(new ApplyEventException(@event, new Exception("boom")), Now);

        failure.Event!.StreamId.ShouldBeNull();
        failure.Event.StreamKey.ShouldBe("trip-1");
    }

    [Fact]
    public void classifies_a_store_serialization_failure_from_its_own_declared_category()
    {
        // The store owns this exception (Marten's EventDeserializationFailureException, Polecat's
        // equivalent) and declares its own category — the daemon never sniffs type names. A failure
        // detected while READING a row knows only the sequence and the stored type alias, which is
        // exactly why every member but Sequence is nullable.
        var failure = ShardFailure.For(
            new FakeStoreEventFailure(ShardFailureCategory.EventSerialization, 77, "trip_started",
                new FormatException("unexpected token")), Now);

        failure.Category.ShouldBe(ShardFailureCategory.EventSerialization);
        failure.Event.ShouldNotBeNull();
        failure.Event.Sequence.ShouldBe(77);
        failure.Event.EventTypeName.ShouldBe("trip_started");
        failure.Event.EventId.ShouldBeNull();
        failure.Event.StreamId.ShouldBeNull();
        failure.Event.TenantId.ShouldBeNull();
        failure.Event.Version.ShouldBeNull();
    }

    [Fact]
    public void classifies_an_unknown_event_type_separately_from_serialization()
    {
        // Different operator action entirely: a missing registration or a rollback, not bad data.
        var failure = ShardFailure.For(
            new FakeStoreEventFailure(ShardFailureCategory.UnknownEventType, 9, "trip_ended", null), Now);

        failure.Category.ShouldBe(ShardFailureCategory.UnknownEventType);
        failure.Event!.Sequence.ShouldBe(9);
    }

    [Fact]
    public void classifies_an_out_of_order_progression()
    {
        // The daemon STOPS rather than pauses on this one, and no single event is to blame.
        var failure = ShardFailure.For(new ProgressionProgressOutOfOrderException("Trip", 100, 90), Now);

        failure.Category.ShouldBe(ShardFailureCategory.ProgressionOutOfOrder);
        failure.Event.ShouldBeNull();
    }

    [Fact]
    public void everything_else_is_other_with_no_event()
    {
        var failure = ShardFailure.For(new TimeoutException("the database went away"), Now);

        failure.Category.ShouldBe(ShardFailureCategory.Other);
        failure.Event.ShouldBeNull();
        failure.Message.ShouldBe("the database went away");
    }

    [Fact]
    public void finds_the_failing_event_through_a_wrapping_exception()
    {
        // The per-event exceptions routinely arrive wrapped — SubscriptionAgent.StopAndDrainAsync throws
        // ShardStopException around whatever it caught. A wrapper must not degrade the classification to
        // "Other", which is the whole point of walking the graph.
        var inner = new ApplyEventException(anEvent(51), new InvalidOperationException("nope"));
        var failure = ShardFailure.For(new ShardStopException("Trip:All", inner), Now);

        failure.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        failure.Event!.Sequence.ShouldBe(51);
        failure.ExceptionType.ShouldBe(typeof(ShardStopException).FullNameInCode());
        failure.RootExceptionType.ShouldBe(typeof(InvalidOperationException).FullNameInCode());
    }

    [Fact]
    public void an_aggregate_of_apply_failures_reports_the_lowest_sequence()
    {
        // A batch can fail on several events at once. The shard stops at the earliest one, so that is the
        // event an operator needs to fix first; the rest are still in Detail.
        var aggregate = new AggregateException(
            new ApplyEventException(anEvent(90), new Exception("third")),
            new ApplyEventException(anEvent(60), new Exception("first")),
            new ApplyEventException(anEvent(75), new Exception("second")));

        var failure = ShardFailure.For(aggregate, Now);

        failure.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        failure.Event!.Sequence.ShouldBe(60);
    }

    [Fact]
    public void an_aggregate_with_no_event_failure_still_finds_a_progression_conflict()
    {
        var aggregate = new AggregateException(
            new TimeoutException("timeout"),
            new ProgressionProgressOutOfOrderException("Trip", 100, 90));

        ShardFailure.For(aggregate, Now).Category.ShouldBe(ShardFailureCategory.ProgressionOutOfOrder);
    }

    [Fact]
    public void detail_is_the_full_exception_text()
    {
        // ShardState.PauseReason has always been ex.ToString(); nothing an operator could read before may
        // be lost by routing it through ShardFailure.
        var ex = new ApplyEventException(anEvent(), new DivideByZeroException("boom"));
        var failure = ShardFailure.For(ex, Now);

        failure.Detail.ShouldBe(ex.ToString());
        failure.Message.ShouldBe(ex.Message);
    }

    #endregion

    #region the agent surface

    [Fact]
    public async Task a_paused_agent_exposes_the_classified_reason()
    {
        await using var harness = new AgentHarness();

        await harness.Agent.ReportCriticalFailureAsync(
            new ApplyEventException(anEvent(31), new DivideByZeroException("boom")));

        harness.Agent.Status.ShouldBe(AgentStatus.Paused);

        // THE issue: Status alone said "paused" and the supervisor had to guess.
        var failure = harness.Agent.Failure.ShouldNotBeNull();
        failure.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        failure.Event!.Sequence.ShouldBe(31);
    }

    [Fact]
    public async Task a_stopped_on_progression_conflict_agent_exposes_its_own_category()
    {
        await using var harness = new AgentHarness();

        await harness.Agent.ReportCriticalFailureAsync(new ProgressionProgressOutOfOrderException("Trip", 10, 5));

        harness.Agent.Status.ShouldBe(AgentStatus.Stopped);
        harness.Agent.Failure!.Category.ShouldBe(ShardFailureCategory.ProgressionOutOfOrder);
    }

    [Fact]
    public async Task the_failure_rides_along_on_the_published_shard_state()
    {
        await using var harness = new AgentHarness();

        await harness.Agent.ReportCriticalFailureAsync(
            new ApplyEventException(anEvent(31), new DivideByZeroException("boom")));

        await harness.Tracker.Complete();

        var state = harness.Tracker.CurrentState("Trip:All").ShouldNotBeNull();
        state.Action.ShouldBe(ShardAction.Paused);
        state.Failure!.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        state.Failure.Event!.Sequence.ShouldBe(31);

        // The pre-existing string surface keeps carrying the same full text it always did.
        state.PauseReason.ShouldBe(state.Failure.Detail);
        state.Exception.ShouldBeOfType<ApplyEventException>();
    }

    [Fact]
    public async Task starting_clears_a_stale_failure()
    {
        // Otherwise a supervisor polling the agent keeps alerting on a failure the operator already fixed.
        await using var harness = new AgentHarness();

        await harness.Agent.ReportCriticalFailureAsync(new TimeoutException("boom"));
        harness.Agent.Failure.ShouldNotBeNull();

        await harness.Agent.StartAsync(new SubscriptionExecutionRequest(0, ShardExecutionMode.Continuous,
            new ErrorHandlingOptions(), Substitute.For<IDaemonRuntime>()));

        harness.Agent.Failure.ShouldBeNull();
    }

    [Fact]
    public void an_agent_that_does_not_track_failures_is_unaffected()
    {
        // The property is a default interface member precisely so wrappers and test doubles compile
        // unchanged; a substitute reports null rather than failing to implement anything.
        Substitute.For<ISubscriptionAgent>().Failure.ShouldBeNull();
    }

    #endregion

    #region the tracker snapshot

    [Fact]
    public async Task the_tracker_hands_out_a_synchronous_snapshot()
    {
        // Before this the only public surface was Subscribe (you had to be listening BEFORE the transition)
        // or a blocking wait. An external poller on its own schedule had nothing to read.
        var tracker = new ShardStateTracker(new NulloLogger());

        try
        {
            tracker.CurrentState("Trip:All").ShouldBeNull();
            tracker.TryGetCurrentState("Trip:All", out _).ShouldBeFalse();

            await tracker.PublishAsync(new ShardState("Trip:All", 30) { AgentStatus = "Running" });
            await tracker.PublishAsync(new ShardState("Other:All", 12) { AgentStatus = "Running" });

            // The snapshot is only as current as the tracker's publication loop, so poll it rather than
            // assuming a posted state has already been consumed. (WaitForShardState is no good here: it
            // checks the map once and then waits for the NEXT publication, so a state consumed in between
            // is missed — and Complete() would close the block for the rest of the test.)
            await waitForCurrent(tracker, "Trip:All", 30);
            await waitForCurrent(tracker, "Other:All", 12);

            tracker.CurrentState(new ShardName("Trip"))!.Sequence.ShouldBe(30);
            tracker.TryGetCurrentState("Trip:All", out var found).ShouldBeTrue();
            found!.Sequence.ShouldBe(30);

            tracker.CurrentStates().Select(x => x.ShardName).OrderBy(x => x)
                .ShouldBe(["Other:All", "Trip:All"]);

            // Latest publication per shard wins
            await tracker.PublishAsync(new ShardState("Trip:All", 45) { AgentStatus = "Running" });
            (await waitForCurrent(tracker, "Trip:All", 45)).Sequence.ShouldBe(45);
        }
        finally
        {
            tracker.As<IDisposable>().Dispose();
        }
    }

    private static async Task<ShardState> waitForCurrent(ShardStateTracker tracker, string shardName, long sequence)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = tracker.CurrentState(shardName);
            if (state != null && state.Sequence >= sequence) return state;

            await Task.Delay(10);
        }

        throw new TimeoutException($"{shardName} never reached sequence {sequence} in the tracker's snapshot");
    }

    #endregion

    #region dead letter correlation

    [Fact]
    public void a_dead_letter_gets_its_identity_at_construction()
    {
        // Assigned here rather than by the store's document identity generation, so the id is known to the
        // process that created it before the background, retried write lands.
        var deadLetter = new DeadLetterEvent(anEvent(), new ShardName("Trip"),
            new ApplyEventException(anEvent(), new Exception("boom")));

        deadLetter.Id.ShouldNotBe(Guid.Empty);
        new DeadLetterEvent(anEvent(), new ShardName("Trip"),
                new ApplyEventException(anEvent(), new Exception("boom"))).Id
            .ShouldNotBe(deadLetter.Id);
    }

    [Fact]
    public void a_dead_letter_correlates_with_the_failure_for_the_same_event()
    {
        var shard = new ShardName("Trip");
        var @event = anEvent(88);
        var applyError = new ApplyEventException(@event, new Exception("boom"));

        var deadLetter = new DeadLetterEvent(@event, shard, applyError);
        var failure = ShardFailure.For(applyError, Now);

        deadLetter.DescribesSameFailureAs(shard, failure).ShouldBeTrue();

        // ... and does not claim failures it has nothing to do with
        deadLetter.DescribesSameFailureAs(new ShardName("Other"), failure).ShouldBeFalse();
        deadLetter.DescribesSameFailureAs(shard,
            ShardFailure.For(new ApplyEventException(anEvent(89), new Exception("boom")), Now)).ShouldBeFalse();
        deadLetter.DescribesSameFailureAs(shard, ShardFailure.For(new TimeoutException("boom"), Now))
            .ShouldBeFalse();
    }

    [Fact]
    public void an_unknown_tenant_does_not_veto_a_correlation()
    {
        // A serialization failure caught while reading the row may not know the tenant. The sequence
        // already established the match; a null tenant must not throw it away.
        var shard = new ShardName("Trip");
        var deadLetter = new DeadLetterEvent(anEvent(88), shard,
            new ApplyEventException(anEvent(88), new Exception("boom")));

        var failure = ShardFailure.For(
            new FakeStoreEventFailure(ShardFailureCategory.EventSerialization, 88, "a_event", null), Now);

        failure.Event!.TenantId.ShouldBeNull();
        deadLetter.DescribesSameFailureAs(shard, failure).ShouldBeTrue();
    }

    [Fact]
    public void a_different_tenant_on_the_same_sequence_is_not_a_match()
    {
        // Under per-tenant event partitioning sequences are per tenant, so the same number is a different
        // event in a different tenant.
        var shard = new ShardName("Trip");
        var @event = anEvent(88);
        var deadLetter = new DeadLetterEvent(@event, shard, new ApplyEventException(@event, new Exception("boom")));

        var otherTenantEvent = anEvent(88);
        otherTenantEvent.TenantId = "tenant2";

        deadLetter.DescribesSameFailureAs(shard,
                ShardFailure.For(new ApplyEventException(otherTenantEvent, new Exception("boom")), Now))
            .ShouldBeFalse();
    }

    #endregion

    // Stands in for a store-owned exception (Marten's EventDeserializationFailureException,
    // UnknownEventTypeException, and Polecat's equivalents). Deliberately minimal: it demonstrates the
    // whole contract those stores have to satisfy — declare a category, report a sequence, and supply
    // whatever else happens to be known.
    private sealed class FakeStoreEventFailure : Exception, IEventFailureContext
    {
        public FakeStoreEventFailure(ShardFailureCategory category, long sequence, string eventTypeName,
            Exception? innerException)
            : base($"Failure on sequence {sequence} for event type {eventTypeName}", innerException)
        {
            Category = category;
            Sequence = sequence;
            EventTypeName = eventTypeName;
        }

        public ShardFailureCategory Category { get; }
        public long Sequence { get; }
        public string? EventTypeName { get; }
        public Guid? EventId => null;
        public Guid? StreamId => null;
        public string? StreamKey => null;
        public string? TenantId => null;
        public long? Version => null;
    }

    private sealed class AgentHarness : IAsyncDisposable
    {
        public AgentHarness()
        {
            Tracker = new ShardStateTracker(new NulloLogger());
            Agent = new SubscriptionAgent(new ShardName("Trip"), new AsyncOptions(), TimeProvider.System,
                Substitute.For<IEventLoader>(), Substitute.For<ISubscriptionExecution>(), Tracker,
                Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);
        }

        public ShardStateTracker Tracker { get; }
        public SubscriptionAgent Agent { get; }

        public ValueTask DisposeAsync()
        {
            Tracker.As<IDisposable>().Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
