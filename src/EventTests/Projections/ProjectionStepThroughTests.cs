using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Projections.ContainerScoped;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#1049: the step-through lifted from Polecat's and Fisher's DocumentStore.ProjectionReplay and
// Marten's explorer. The fold mutates an aggregate in place, so everything that follows is about what
// gets recorded around it. Everything here goes through ProjectionGraph.StepThrough, as a store will.
public class ProjectionStepThroughTests
{
    private static readonly Guid Stream = Guid.NewGuid();

    private static long _sequence;

    private static readonly ProjectionStateSerializer States = new(
        state => JsonSerializer.SerializeToElement(state, state.GetType()),
        (json, type) => json.Deserialize(type));

    private static EventRecord record(object body, Guid? stream = null, string? typeName = null)
    {
        var sequence = Interlocked.Increment(ref _sequence);

        return new EventRecord(Guid.NewGuid(), sequence, sequence, (stream ?? Stream).ToString(),
            typeName ?? body.GetType().Name, JsonSerializer.SerializeToElement(body, body.GetType()), null,
            DateTimeOffset.UtcNow, null, null);
    }

    private static IEvent wrap<T>(EventRecord source) where T : notnull
        => new Event<T>(source.Data.Deserialize<T>()!).WithRecordMetadata(source, StreamIdentity.AsGuid);

    // The shape of every store's ToDomainEvent: null for a type this process does not know.
    private static IEvent? toEvent(EventRecord source) => source.EventTypeName switch
    {
        nameof(Deposited) => wrap<Deposited>(source),
        nameof(Withdrawn) => wrap<Withdrawn>(source),
        nameof(Misapplied) => wrap<Misapplied>(source),
        nameof(Closed) => wrap<Closed>(source),
        nameof(Rebuked) => wrap<Rebuked>(source),
        nameof(Batch) => wrap<Batch>(source),
        nameof(Compacted<Ledger>) => wrap<Compacted<Ledger>>(source),
        _ => null
    };

    private static ProjectionStepThrough<FakeSession> stepThroughOf(object projection)
    {
        var graph = new StepGraph();
        graph.All.Add((IProjectionSource<FakeOperations, FakeSession>)projection);
        return graph.StepThrough(toEvent, States);
    }

    private static ProjectionStepThrough<FakeSession> ledger() => stepThroughOf(new LedgerProjection());

    private static Task<ProjectionTimeline<T>> run<T>(ProjectionStepThrough<FakeSession> stepThrough,
        IReadOnlyList<EventRecord> events, T? start = default)
        => stepThrough.RunProjectionAsync("Ledger", Stream, events, start, new FakeSession(), CancellationToken.None);

    private static Task<ProjectionTimelineRaw> runRaw(ProjectionStepThrough<FakeSession> stepThrough,
        IReadOnlyList<EventRecord> events, JsonElement? start = null)
        => stepThrough.RunProjectionByNameAsync("Ledger", Stream, events, start, new FakeSession(),
            CancellationToken.None);

    private static int balance(JsonElement? state) => state!.Value.GetProperty(nameof(Ledger.Balance)).GetInt32();

    // ----- The bug the lift exists to fix

    [Fact]
    public async Task every_typed_step_of_a_mutable_aggregate_holds_the_state_as_of_that_step()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Deposited(10)), record(new Deposited(5)), record(new Withdrawn(3))]);

        timeline.Steps.Select(x => x.After!.Balance).ShouldBe([10, 15, 12]);
        timeline.Steps.Select(x => x.Before?.Balance).ShouldBe([null, 10, 15]);

        // A member reached through a reference is the part a shallow copy would still share.
        timeline.Steps.Select(x => x.After!.Entries.Count).ShouldBe([1, 2, 3]);
        timeline.Steps[1].After!.Entries.ShouldBe(["+10", "+5"]);

        timeline.FinalState!.Balance.ShouldBe(12);
    }

    [Fact]
    public async Task every_step_by_name_holds_the_state_as_of_that_step()
    {
        var timeline = await runRaw(ledger(), [record(new Deposited(10)), record(new Deposited(5)), record(new Withdrawn(3))]);

        timeline.Steps.Select(x => balance(x.After)).ShouldBe([10, 15, 12]);
        timeline.Steps[0].Before.ShouldBeNull();
        balance(timeline.Steps[2].Before).ShouldBe(15);
        balance(timeline.FinalState).ShouldBe(12);
    }

    [Fact]
    public async Task every_step_of_a_record_aggregate_holds_the_state_as_of_that_step()
    {
        var timeline = await run<Tally>(stepThroughOf(new TallyProjection()),
            [record(new Deposited(1)), record(new Deposited(2)), record(new Deposited(3))]);

        timeline.Steps.Select(x => x.After!.Total).ShouldBe([1, 3, 6]);
        timeline.Steps.Select(x => x.Before?.Total).ShouldBe([null, 1, 3]);
    }

    [Fact]
    public async Task an_aggregate_comes_into_existence_on_the_step_that_creates_it()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Deposited(1))]);

        timeline.Steps[0].Before.ShouldBeNull();
        timeline.Steps[0].After.ShouldNotBeNull();
    }

    [Fact]
    public async Task the_recorded_states_do_not_share_anything_with_each_other_across_events()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Deposited(1)), record(new Deposited(2))]);

        timeline.Steps[0].After!.Entries.Add("tampered");

        timeline.Steps[1].After!.Entries.ShouldBe(["+1", "+2"]);
    }

    // ----- Deletes, which IAggregator.BuildAsync cannot report

    [Fact]
    public async Task a_declared_delete_event_ends_the_aggregate_on_that_step()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Deposited(4)), record(new Closed())]);

        timeline.Steps[1].Before!.Balance.ShouldBe(4);
        timeline.Steps[1].After.ShouldBeNull();
        timeline.Steps[1].Error.ShouldBeNull();
        timeline.FinalState.ShouldBeNull();
    }

    [Fact]
    public async Task a_declared_delete_event_is_reported_by_name_too()
    {
        var timeline = await runRaw(ledger(), [record(new Deposited(4)), record(new Closed())]);

        timeline.Steps[1].After.ShouldBeNull();
        timeline.FinalState.ShouldBeNull();
    }

    [Fact]
    public async Task an_aggregate_can_come_back_after_a_delete()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Deposited(4)), record(new Closed()), record(new Deposited(9))]);

        timeline.Steps[2].Before.ShouldBeNull();
        timeline.Steps[2].After!.Balance.ShouldBe(9);
        timeline.FinalState!.Balance.ShouldBe(9);
    }

    [Fact]
    public async Task a_delete_with_nothing_to_delete_changes_nothing()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Closed())]);

        timeline.Steps[0].Before.ShouldBeNull();
        timeline.Steps[0].After.ShouldBeNull();
        timeline.Steps[0].Error.ShouldBeNull();
    }

    [Fact]
    public async Task an_evolve_returning_null_is_a_delete()
    {
        var timeline = await run<Tally>(stepThroughOf(new TallyProjection()), [record(new Deposited(1)), record(new Closed())]);

        timeline.Steps[1].After.ShouldBeNull();
        timeline.FinalState.ShouldBeNull();
    }

    [Theory]
    [InlineData(ActionType.Delete)]
    [InlineData(ActionType.HardDelete)]
    public async Task a_delete_decided_by_the_projection_is_a_delete(ActionType action)
    {
        var timeline = await run<Tally>(stepThroughOf(new DecidingProjection(action)), [record(new Deposited(1)), record(new Closed())]);

        timeline.Steps[0].After!.Total.ShouldBe(1);
        timeline.Steps[1].After.ShouldBeNull();
        timeline.FinalState.ShouldBeNull();
    }

    [Theory]
    [InlineData(ActionType.Delete)]
    [InlineData(ActionType.HardDelete)]
    public async Task a_delete_decided_by_the_projection_is_a_delete_in_the_fan_out_fold(ActionType action)
    {
        var result = await stepThroughOf(new DecidingProjection(action)).RunMultiStreamProjectionAsync("Ledger",
            [record(new Deposited(1)), record(new Closed())], new FakeSession(), CancellationToken.None);

        var timeline = result.AggregatesByIdentity[Stream.ToString()];
        timeline.Steps[1].After.ShouldBeNull();
        timeline.FinalState.ShouldBeNull();
    }

    // ----- The serializer seam

    [Fact]
    public void the_seam_cannot_be_left_empty()
    {
        Should.Throw<ArgumentNullException>(() => new ProjectionStateSerializer(null!, (_, _) => null));
        Should.Throw<ArgumentNullException>(() => new ProjectionStateSerializer(_ => default, null!));

        var graph = new StepGraph();
        Should.Throw<ArgumentNullException>(() => graph.StepThrough(toEvent, null!));
        Should.Throw<ArgumentNullException>(() => graph.StepThrough(null!, States));
    }

    [Fact]
    public async Task a_starting_state_is_not_changed_by_the_fold()
    {
        var start = new Ledger { Id = Stream, Balance = 100, Entries = ["opening"] };

        var timeline = await run(ledger(), [record(new Deposited(5))], start);

        timeline.Steps[0].Before!.Balance.ShouldBe(100);
        timeline.FinalState!.Balance.ShouldBe(105);

        start.Balance.ShouldBe(100);
        start.Entries.ShouldBe(["opening"]);
    }

    [Fact]
    public async Task a_previous_timeline_can_be_continued_without_changing_it()
    {
        var page1 = await run<Ledger>(ledger(), [record(new Deposited(1)), record(new Deposited(2))]);
        var page2 = await run(ledger(), [record(new Deposited(4))], page1.FinalState);

        page2.Steps[0].Before!.Balance.ShouldBe(3);
        page2.FinalState!.Balance.ShouldBe(7);

        page1.FinalState!.Balance.ShouldBe(3);
    }

    [Fact]
    public async Task no_events_leaves_the_starting_state_as_the_final_state()
    {
        var start = new Ledger { Id = Stream, Balance = 8 };

        var timeline = await run(ledger(), [], start);

        timeline.Steps.ShouldBeEmpty();
        timeline.FinalState!.Balance.ShouldBe(8);
        timeline.FinalState.ShouldNotBeSameAs(start);
    }

    [Fact]
    public async Task no_events_and_no_starting_state_is_no_state()
    {
        (await run<Ledger>(ledger(), [])).FinalState.ShouldBeNull();
        (await runRaw(ledger(), [])).FinalState.ShouldBeNull();
    }

    [Fact]
    public async Task a_json_starting_state_seeds_the_fold()
    {
        var start = JsonSerializer.SerializeToElement(new Ledger { Id = Stream, Balance = 50 });

        var timeline = await runRaw(ledger(), [record(new Deposited(1))], start);

        balance(timeline.Steps[0].Before).ShouldBe(50);
        balance(timeline.FinalState).ShouldBe(51);
    }

    [Fact]
    public async Task state_the_serializer_does_not_write_still_counts_in_the_fold()
    {
        // The fold runs on the live instance and only the record goes through the serializer. Had the
        // fold continued from a copy, Scale would be gone after the first step and every later deposit
        // would count once.
        var timeline = await run<Scaled>(stepThroughOf(new ScaledProjection()),
            [record(new Deposited(1)), record(new Deposited(1)), record(new Deposited(1))]);

        timeline.Steps.Select(x => x.After!.Total).ShouldBe([10, 20, 30]);

        // What a step shows is what would be persisted, and this would not be.
        timeline.Steps[2].After!.Scale.ShouldBe(0);
    }

    [Fact]
    public async Task state_the_serializer_reads_back_differently_is_refused_rather_than_reported()
    {
        var timeline = () => run<PrivatelySet>(stepThroughOf(new PrivatelySetProjection()), [record(new Deposited(7))]);

        var ex = await Should.ThrowAsync<ProjectionStateRoundTripException>(timeline);

        ex.StateType.ShouldBe(typeof(PrivatelySet));
        ex.Event!.EventTypeName.ShouldBe(nameof(Deposited));
        ex.Message.ShouldContain(nameof(PrivatelySet.Total));
        ex.Message.ShouldContain("came back different");

        // Names the member, never its value: this message is on its way to a log.
        ex.Message.ShouldNotContain("7 ");
    }

    [Fact]
    public async Task by_name_needs_no_trip_so_it_answers_for_a_state_the_typed_form_refuses()
    {
        var timeline = await runRaw(stepThroughOf(new PrivatelySetProjection()),
            [record(new Deposited(7)), record(new Deposited(1))]);

        timeline.Steps.Select(x => x.After!.Value.GetProperty("Total").GetInt32()).ShouldBe([7, 8]);
    }

    [Fact]
    public async Task state_that_cannot_be_deserialized_says_so()
    {
        var ex = await Should.ThrowAsync<ProjectionStateRoundTripException>(
            () => run<Unreadable>(stepThroughOf(new UnreadableProjection()), [record(new Deposited(1))]));

        ex.InnerException.ShouldNotBeNull();
        ex.Message.ShouldContain("could not be deserialized");
    }

    [Fact]
    public async Task state_that_cannot_be_serialized_says_so_on_either_path()
    {
        var failing = new ProjectionStateSerializer(_ => throw new DivideByZeroException(), (_, _) => null);

        var graph = new StepGraph();
        graph.All.Add(new LedgerProjection());
        var stepThrough = graph.StepThrough(toEvent, failing);

        var ex = await Should.ThrowAsync<ProjectionStateRoundTripException>(
            () => runRaw(stepThrough, [record(new Deposited(1))]));

        ex.InnerException.ShouldBeOfType<DivideByZeroException>();
    }

    [Fact]
    public async Task a_deserializer_that_answers_null_does_not_pass_for_a_copy()
    {
        var empty = new ProjectionStateSerializer(state => JsonSerializer.SerializeToElement(state), (_, _) => null);

        var graph = new StepGraph();
        graph.All.Add(new LedgerProjection());

        var ex = await Should.ThrowAsync<ProjectionStateRoundTripException>(
            () => run<Ledger>(graph.StepThrough(toEvent, empty), [record(new Deposited(1))]));

        ex.Message.ShouldContain("deserialized to null");
    }

    [Fact]
    public async Task a_starting_state_that_cannot_make_the_trip_is_refused_before_folding()
    {
        var start = PrivatelySet.Holding(3);

        var ex = await Should.ThrowAsync<ProjectionStateRoundTripException>(
            () => run(stepThroughOf(new PrivatelySetProjection()), [record(new Deposited(1))], start));

        ex.Event.ShouldBeNull();
    }

    // ----- Records toEvent cannot read

    [Fact]
    public async Task an_event_this_process_does_not_know_is_a_step_that_changed_nothing()
    {
        var unknown = record(new Deposited(99), typeName: "from_a_newer_deployment");

        var timeline = await run<Ledger>(ledger(), [record(new Deposited(1)), unknown, record(new Deposited(2))]);

        timeline.Steps.Count.ShouldBe(3);

        var skipped = timeline.Steps[1];
        skipped.Event.ShouldBeSameAs(unknown);
        skipped.Error.ShouldBeNull();
        skipped.Before!.Balance.ShouldBe(1);
        skipped.After!.Balance.ShouldBe(1);

        timeline.FinalState!.Balance.ShouldBe(3);
    }

    [Fact]
    public async Task an_event_this_process_does_not_know_before_anything_exists_changes_nothing()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Deposited(99), typeName: "unknown")]);

        timeline.Steps.Count.ShouldBe(1);
        timeline.Steps[0].After.ShouldBeNull();
        timeline.FinalState.ShouldBeNull();
    }

    [Fact]
    public async Task an_event_this_process_does_not_know_is_left_out_of_the_fan_out()
    {
        var result = await ledger().RunMultiStreamProjectionAsync("Ledger",
            [record(new Deposited(1)), record(new Deposited(99), typeName: "unknown"), record(new Deposited(2))],
            new FakeSession(), CancellationToken.None);

        var timeline = result.AggregatesByIdentity[Stream.ToString()];
        timeline.Steps.Count.ShouldBe(2);
        balance(timeline.FinalState).ShouldBe(3);
    }

    [Fact]
    public async Task a_record_that_cannot_be_read_at_all_is_an_error_on_its_step()
    {
        var corrupt = record(new Deposited(1)) with { Data = JsonSerializer.SerializeToElement("not a deposit") };

        var timeline = await run<Ledger>(ledger(), [record(new Deposited(1)), corrupt, record(new Deposited(2))]);

        timeline.Steps[1].Error.ShouldNotBeNull();
        timeline.Steps[1].After!.Balance.ShouldBe(1);
        timeline.FinalState!.Balance.ShouldBe(3);
    }

    // ----- Failures

    [Fact]
    public async Task a_failed_apply_is_recorded_as_the_projections_own_exception_and_the_fold_continues()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Deposited(5)), record(new Rebuked()), record(new Deposited(2))]);

        timeline.Steps[1].Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("rebuked");
        timeline.Steps[1].After!.Balance.ShouldBe(timeline.Steps[1].Before!.Balance);

        timeline.Steps[2].After!.Balance.ShouldBe(7);
    }

    [Fact]
    public async Task a_failed_apply_reports_its_message_by_name()
    {
        var timeline = await runRaw(ledger(), [record(new Deposited(5)), record(new Rebuked())]);

        timeline.Steps[1].Error.ShouldBe("rebuked");
    }

    [Fact]
    public async Task a_failure_on_the_first_event_leaves_nothing()
    {
        var timeline = await run<Ledger>(ledger(), [record(new Rebuked()), record(new Deposited(2))]);

        timeline.Steps[0].Error.ShouldNotBeNull();
        timeline.Steps[0].After.ShouldBeNull();
        timeline.Steps[1].After!.Balance.ShouldBe(2);
    }

    [Fact]
    public async Task an_apply_that_fails_half_way_through_leaves_no_trace()
    {
        // Mutates, then throws. The event is reported as failed, so nothing it did may survive it.
        var timeline = await run<Ledger>(ledger(),
            [record(new Deposited(5)), record(new Misapplied()), record(new Deposited(2))]);

        var failed = timeline.Steps[1];
        failed.Error.ShouldBeOfType<DivideByZeroException>();
        failed.After!.Entries.ShouldBe(["+5"]);
        failed.After.Entries.ShouldBe(failed.Before!.Entries);

        timeline.Steps[2].Before!.Entries.ShouldBe(["+5"]);
        timeline.FinalState!.Entries.ShouldBe(["+5", "+2"]);
    }

    [Fact]
    public async Task an_apply_that_fails_half_way_through_leaves_no_trace_in_the_fan_out()
    {
        var result = await ledger().RunMultiStreamProjectionAsync("Ledger",
            [record(new Deposited(5)), record(new Misapplied()), record(new Deposited(2))],
            new FakeSession(), CancellationToken.None);

        var timeline = result.AggregatesByIdentity[Stream.ToString()];

        timeline.Steps[1].Error.ShouldNotBeNull();
        balance(timeline.Steps[1].After).ShouldBe(5);
        timeline.FinalState!.Value.Deserialize<Ledger>()!.Entries.ShouldBe(["+5", "+2"]);
    }

    [Fact]
    public async Task a_failed_apply_that_changed_nothing_stays_on_the_live_instance()
    {
        // Restoring goes through the serializer and would cost the fold its Scale. There is no reason to
        // pay that when the apply threw before touching anything.
        var timeline = await run<Scaled>(stepThroughOf(new ScaledProjection()),
            [record(new Deposited(1)), record(new Rebuked()), record(new Deposited(1))]);

        timeline.Steps[1].Error.ShouldNotBeNull();
        timeline.FinalState!.Total.ShouldBe(20);
    }

    [Fact]
    public async Task the_original_delegate_overload_has_no_way_back_and_reports_what_happened_instead()
    {
        var projection = new LedgerProjection();
        var events = new[]
        {
            new Event<Deposited>(new Deposited(5)) { StreamId = Stream, Sequence = 1, Version = 1 },
            (IEvent)new Event<Misapplied>(new Misapplied()) { StreamId = Stream, Sequence = 2, Version = 2 }
        };

        var result = await projection.BuildTimelinesAsync(events, new FakeSession(),
            state => state is null ? null : JsonSerializer.SerializeToElement(state, state.GetType()),
            e => record(e.Data), null, CancellationToken.None);

        var failed = result.AggregatesByIdentity[Stream.ToString()].Steps[1];

        failed.Error.ShouldNotBeNull();
        failed.After!.Value.Deserialize<Ledger>()!.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task cancellation_stops_the_replay_rather_than_landing_on_a_step()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => ledger().RunProjectionAsync<Ledger>("Ledger", Stream,
            [record(new Deposited(1))], null, new FakeSession(), cancellation.Token));
    }

    [Fact]
    public async Task cancellation_from_inside_an_apply_stops_the_replay_on_every_path()
    {
        using var cancellation = new CancellationTokenSource();
        var stepThrough = stepThroughOf(new CancellingProjection(cancellation));

        await Should.ThrowAsync<OperationCanceledException>(() => stepThrough.RunProjectionByNameAsync("Ledger", Stream,
            [record(new Deposited(1))], null, new FakeSession(), cancellation.Token));
    }

    [Fact]
    public async Task cancellation_from_inside_an_apply_stops_the_fan_out_too()
    {
        using var cancellation = new CancellationTokenSource();
        var stepThrough = stepThroughOf(new CancellingProjection(cancellation));

        await Should.ThrowAsync<OperationCanceledException>(() => stepThrough.RunMultiStreamProjectionAsync("Ledger",
            [record(new Deposited(1))], new FakeSession(), cancellation.Token));
    }

    [Fact]
    public async Task an_operation_cancelled_on_its_own_is_only_a_failed_apply()
    {
        // A timeout inside an apply throws the same exception type, and nobody asked this replay to stop.
        var timeline = await run<Ledger>(stepThroughOf(new CancellingProjection(null)), [record(new Deposited(1))]);

        timeline.Steps[0].Error.ShouldBeOfType<OperationCanceledException>();
    }

    // ----- Fidelity to what the daemon would have done

    [Fact]
    public async Task nothing_raises_side_effects_and_the_query_session_is_what_applies_receive()
    {
        var projection = new SideEffectingProjection();
        var session = new FakeSession();

        var stepThrough = stepThroughOf(projection);
        await stepThrough.RunProjectionAsync<Ledger>("Ledger", Stream, [record(new Deposited(1))], null, session, CancellationToken.None);
        await stepThrough.RunProjectionByNameAsync("Ledger", Stream, [record(new Deposited(1))], null, session, CancellationToken.None);
        await stepThrough.RunMultiStreamProjectionAsync("Ledger", [record(new Deposited(1))], session, CancellationToken.None);

        projection.SideEffectsRaised.ShouldBe(0);
        projection.Sessions.ShouldBe([session, session, session]);
    }

    [Fact]
    public async Task a_step_carries_the_version_the_daemon_would_stamp()
    {
        var events = new[] { record(new Deposited(1)), record(new Deposited(1)) };

        var timeline = await run<Ledger>(ledger(), events);

        timeline.Steps.Select(x => x.After!.Version).ShouldBe(events.Select(x => (int)x.StreamVersion));
    }

    [Fact]
    public async Task the_fan_out_carries_the_version_the_daemon_would_stamp()
    {
        var events = new[] { record(new Deposited(1)), record(new Deposited(1)) };

        var result = await ledger().RunMultiStreamProjectionAsync("Ledger", events, new FakeSession(), CancellationToken.None);

        var timeline = result.AggregatesByIdentity[Stream.ToString()];
        timeline.Steps.Select(x => x.After!.Value.GetProperty(nameof(Ledger.Version)).GetInt32())
            .ShouldBe(events.Select(x => (int)x.StreamVersion));
    }

    [Fact]
    public async Task a_compacted_marker_restarts_the_fold_from_the_snapshot_it_carries()
    {
        var compacted = new Compacted<Ledger>(new Ledger { Id = Stream, Balance = 40, Entries = ["history"] }, Stream, "");

        var timeline = await run<Ledger>(ledger(), [record(compacted, typeName: nameof(Compacted<Ledger>)), record(new Deposited(2))]);

        timeline.Steps[0].After!.Balance.ShouldBe(40);
        timeline.FinalState!.Balance.ShouldBe(42);
        timeline.FinalState.Entries.ShouldBe(["history", "+2"]);
    }

    // ----- Finding the projection

    [Fact]
    public async Task an_unknown_projection_is_refused_by_name_on_every_path()
    {
        var stepThrough = ledger();

        var typed = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => stepThrough.RunProjectionAsync<Ledger>("nope", Stream, [], null, new FakeSession(), CancellationToken.None));
        typed.Message.ShouldContain("Unknown projection 'nope'");
        typed.Message.ShouldContain("Available projections: 'Ledger'");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => stepThrough.RunProjectionByNameAsync("nope", Stream, [], null, new FakeSession(), CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => stepThrough.RunMultiStreamProjectionAsync("nope", [], new FakeSession(), CancellationToken.None));
    }

    [Fact]
    public async Task a_refused_name_is_still_an_argument_exception_for_callers_catching_that()
    {
        await Should.ThrowAsync<ArgumentException>(() => ledger().RunMultiStreamProjectionAsync("nope", [], new FakeSession(), CancellationToken.None));
    }

    [Fact]
    public async Task the_state_type_has_to_be_the_one_the_projection_builds()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() => run<Tally>(ledger(), []));

        ex.Message.ShouldContain("is over");
    }

    [Fact]
    public async Task a_projection_with_no_aggregate_is_refused()
    {
        var source = Substitute.For<IProjectionSource<FakeOperations, FakeSession>>();
        source.Name.Returns("Ledger");

        var ex = await Should.ThrowAsync<NotSupportedException>(() => runRaw(stepThroughOf(source), []));

        ex.Message.ShouldContain("'Ledger'");
    }

    [Fact]
    public async Task a_composite_member_is_found_by_its_own_name()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Accounts");
        composite.StageFor(1).Add(new LedgerProjection());

        var timeline = await run<Ledger>(stepThroughOf(composite), [record(new Deposited(1))]);

        timeline.FinalState!.Balance.ShouldBe(1);
    }

    [Theory]
    [InlineData(typeof(ScopedSingleStreamAggregationWrapper<LedgerProjection, Ledger, Guid, FakeOperations, FakeSession>))]
    [InlineData(typeof(ScopedAggregationWrapper<LedgerProjection, Ledger, Guid, FakeOperations, FakeSession>))]
    public async Task a_scoped_projection_steps_through_like_any_other(Type wrapperType)
    {
        var services = new ServiceCollection().AddScoped<LedgerProjection>().BuildServiceProvider();
        var wrapper = Activator.CreateInstance(wrapperType, services)!;
        var stepThrough = stepThroughOf(wrapper);

        var events = new[] { record(new Deposited(1)), record(new Closed()), record(new Deposited(2)) };

        var typed = await run<Ledger>(stepThrough, events);
        typed.Steps.Select(x => x.After?.Balance).ShouldBe([1, null, 2]);

        var raw = await runRaw(stepThrough, events);
        balance(raw.FinalState).ShouldBe(2);

        var fanned = await stepThrough.RunMultiStreamProjectionAsync("Ledger", events, new FakeSession(), CancellationToken.None);
        balance(fanned.AggregatesByIdentity[Stream.ToString()].FinalState).ShouldBe(2);
    }

    // ----- The fan-out and a multi-stream projection

    [Fact]
    public async Task the_fan_out_reports_the_record_the_caller_supplied()
    {
        var supplied = record(new Deposited(1));

        var result = await ledger().RunMultiStreamProjectionAsync("Ledger", [supplied], new FakeSession(), CancellationToken.None);

        result.AggregatesByIdentity[Stream.ToString()].Steps[0].Event.ShouldBeSameAs(supplied);
    }

    [Fact]
    public async Task the_fan_out_splits_a_flat_list_across_streams()
    {
        var other = Guid.NewGuid();

        var result = await ledger().RunMultiStreamProjectionAsync("Ledger",
            [record(new Deposited(1)), record(new Deposited(5), other), record(new Deposited(2))],
            new FakeSession(), CancellationToken.None);

        balance(result.AggregatesByIdentity[Stream.ToString()].FinalState).ShouldBe(3);
        balance(result.AggregatesByIdentity[other.ToString()].FinalState).ShouldBe(5);
    }

    [Fact]
    public async Task an_event_a_fan_out_created_has_a_record_of_its_own()
    {
        // Every step needs a record, and these events have none behind them. Polecat and Fisher threw
        // KeyNotFoundException here and lost the whole timeline.
        var batch = record(new Batch([new Deposited(1), new Deposited(2)]));

        var result = await stepThroughOf(new BatchProjection()).RunMultiStreamProjectionAsync("Batches", [batch],
            new FakeSession(), CancellationToken.None);

        var timeline = result.AggregatesByIdentity["7"];

        timeline.Steps.Select(x => x.Event.EventTypeName).ShouldBe([nameof(Batch), nameof(Deposited), nameof(Deposited)]);
        balance(timeline.FinalState).ShouldBe(3);

        var created = timeline.Steps[1].Event;
        created.EventTypeName.ShouldBe(nameof(Deposited));
        created.Sequence.ShouldBe(batch.Sequence);
        created.Data.GetProperty(nameof(Deposited.Amount)).GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task a_multi_stream_projection_folds_one_aggregate_under_the_identity_it_is_given()
    {
        var timeline = await stepThroughOf(new BatchProjection()).RunProjectionAsync<BatchLedger>("Batches", 7,
            [record(new Deposited(1)), record(new Deposited(2))], null, new FakeSession(), CancellationToken.None);

        timeline.Steps.Select(x => x.After!.Balance).ShouldBe([1, 3]);
        timeline.FinalState!.Id.ShouldBe(7);
    }

    [Fact]
    public async Task a_single_stream_projection_reads_the_identity_off_the_event()
    {
        // What the caller passes is advisory here: Polecat's own test passes a string on a Guid store.
        var timeline = await ledger().RunProjectionAsync<Ledger>("Ledger", "not the identity",
            [record(new Deposited(1))], null, new FakeSession(), CancellationToken.None);

        timeline.FinalState!.Id.ShouldBe(Stream);
    }

    [Fact]
    public async Task a_multi_stream_projection_refuses_an_identity_of_the_wrong_type()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() => stepThroughOf(new BatchProjection())
            .RunProjectionByNameAsync("Batches", Guid.NewGuid(), [record(new Deposited(1))], null, new FakeSession(),
                CancellationToken.None));

        ex.Message.ShouldContain("RunMultiStreamProjectionAsync");
    }

    // ----- Stamping a record onto an event

    [Fact]
    public void a_record_stamps_an_event_on_a_guid_store()
    {
        var source = record(new Deposited(1)) with { TenantId = "green" };

        var @event = new Event<Deposited>(new Deposited(1)).WithRecordMetadata(source, StreamIdentity.AsGuid);

        @event.Id.ShouldBe(source.EventId);
        @event.Sequence.ShouldBe(source.Sequence);
        @event.Version.ShouldBe(source.StreamVersion);
        @event.Timestamp.ShouldBe(source.Timestamp);
        @event.TenantId.ShouldBe("green");
        @event.EventTypeName.ShouldBe(nameof(Deposited));
        @event.DotNetTypeName.ShouldBe(typeof(Deposited).AssemblyQualifiedName);
        @event.StreamId.ShouldBe(Stream);
        @event.StreamKey.ShouldBeNull();
    }

    [Fact]
    public void a_record_stamps_an_event_on_a_string_store()
    {
        var source = record(new Deposited(1)) with { StreamId = "acct-1" };

        var @event = new Event<Deposited>(new Deposited(1)).WithRecordMetadata(source, StreamIdentity.AsString);

        @event.StreamKey.ShouldBe("acct-1");
        @event.StreamId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void a_guid_looking_id_stays_a_string_on_a_string_store()
    {
        var source = record(new Deposited(1));

        var @event = new Event<Deposited>(new Deposited(1)).WithRecordMetadata(source, StreamIdentity.AsString);

        @event.StreamKey.ShouldBe(Stream.ToString());
    }

    [Fact]
    public void a_stream_id_that_is_not_a_guid_is_kept_on_a_guid_store()
    {
        var source = record(new Deposited(1)) with { StreamId = "not-a-guid" };

        var @event = new Event<Deposited>(new Deposited(1)).WithRecordMetadata(source, StreamIdentity.AsGuid);

        @event.StreamKey.ShouldBe("not-a-guid");
    }

    [Fact]
    public void a_record_without_a_tenant_lands_on_the_default_tenant()
    {
        var @event = new Event<Deposited>(new Deposited(1)).WithRecordMetadata(record(new Deposited(1)), StreamIdentity.AsGuid);

        @event.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
    }

    // ----- Fixtures

    private sealed class StepGraph() : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>(
        Substitute.For<IEventRegistry>(), "tests")
    {
        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }

    public record Deposited(int Amount);

    public record Withdrawn(int Amount);

    public record Misapplied;

    public record Closed;

    public record Rebuked;

    public record Batch(Deposited[] Deposits);

    public class Ledger
    {
        public Guid Id { get; set; }
        public int Balance { get; set; }
        public int Version { get; set; }
        public List<string> Entries { get; set; } = [];
    }

    // In place, as JasperFx's aggregation does, which is the whole of the problem.
    public class LedgerProjection : SingleStreamProjection<Ledger, Guid>
    {
        public LedgerProjection()
        {
            ((ProjectionBase)this).Name = "Ledger";
            DeleteEvent<Closed>();
        }

        public override Ledger? Evolve(Ledger? snapshot, Guid id, IEvent e)
        {
            switch (e.Data)
            {
                case Deposited deposited:
                    snapshot ??= new Ledger { Id = id };
                    snapshot.Balance += deposited.Amount;
                    snapshot.Entries.Add($"+{deposited.Amount}");
                    break;

                case Withdrawn withdrawn:
                    snapshot!.Balance -= withdrawn.Amount;
                    snapshot.Entries.Add($"-{withdrawn.Amount}");
                    break;

                case Misapplied:
                    snapshot!.Entries.Add("half done");
                    throw new DivideByZeroException();

                case Rebuked:
                    throw new InvalidOperationException("rebuked");
            }

            return snapshot;
        }
    }

    public record Tally(Guid Id, int Total);

    public class TallyProjection : SingleStreamProjection<Tally, Guid>
    {
        public TallyProjection() => ((ProjectionBase)this).Name = "Ledger";

        public override Tally? Evolve(Tally? snapshot, Guid id, IEvent e) => e.Data switch
        {
            Deposited deposited => new Tally(id, (snapshot?.Total ?? 0) + deposited.Amount),
            Closed => null,
            _ => snapshot
        };
    }

    public class DecidingProjection : SingleStreamProjection<Tally, Guid>
    {
        private readonly ActionType _action;

        public DecidingProjection(ActionType action)
        {
            _action = action;
            ((ProjectionBase)this).Name = "Ledger";
        }

        public override (Tally?, ActionType) DetermineAction(Tally? snapshot, Guid identity, IReadOnlyList<IEvent> events)
            => events[0].Data is Closed
                ? (snapshot, _action)
                : (new Tally(identity, (snapshot?.Total ?? 0) + 1), ActionType.Store);
    }

    public class Scaled
    {
        public Guid Id { get; set; }
        public int Total { get; set; }

        [JsonIgnore] public int Scale { get; set; }
    }

    public class ScaledProjection : SingleStreamProjection<Scaled, Guid>
    {
        public ScaledProjection() => ((ProjectionBase)this).Name = "Ledger";

        public override Scaled? Evolve(Scaled? snapshot, Guid id, IEvent e)
        {
            switch (e.Data)
            {
                case Deposited deposited:
                    snapshot ??= new Scaled { Id = id, Scale = 10 };
                    snapshot.Total += deposited.Amount * snapshot.Scale;
                    break;

                case Rebuked:
                    throw new InvalidOperationException("rebuked");
            }

            return snapshot;
        }
    }

    // Common on a live aggregate that is never read back from storage, and invisible to a serializer
    // until a value comes back as zero.
    public class PrivatelySet
    {
        public Guid Id { get; set; }
        public int Total { get; private set; }

        public void Add(int amount) => Total += amount;

        public static PrivatelySet Holding(int amount)
        {
            var state = new PrivatelySet();
            state.Add(amount);
            return state;
        }
    }

    public class PrivatelySetProjection : SingleStreamProjection<PrivatelySet, Guid>
    {
        public PrivatelySetProjection() => ((ProjectionBase)this).Name = "Ledger";

        public override PrivatelySet? Evolve(PrivatelySet? snapshot, Guid id, IEvent e)
        {
            snapshot ??= new PrivatelySet { Id = id };
            if (e.Data is Deposited deposited) snapshot.Add(deposited.Amount);
            return snapshot;
        }
    }

    // Two constructors and no [JsonConstructor], so System.Text.Json has nothing it can call.
    public class Unreadable
    {
        public Unreadable(int total) => Total = total;
        public Unreadable(string total) => Total = int.Parse(total);

        public int Total { get; }
    }

    public class UnreadableProjection : SingleStreamProjection<Unreadable, Guid>
    {
        public UnreadableProjection() => ((ProjectionBase)this).Name = "Ledger";

        public override Unreadable? Evolve(Unreadable? snapshot, Guid id, IEvent e) => new(1);
    }

    public class CancellingProjection : SingleStreamProjection<Ledger, Guid>
    {
        private readonly CancellationTokenSource? _cancellation;

        public CancellingProjection(CancellationTokenSource? cancellation)
        {
            _cancellation = cancellation;
            ((ProjectionBase)this).Name = "Ledger";
        }

        public override Ledger? Evolve(Ledger? snapshot, Guid id, IEvent e)
        {
            _cancellation?.Cancel();
            throw new OperationCanceledException();
        }
    }

    public class SideEffectingProjection : SingleStreamProjection<Ledger, Guid>
    {
        public SideEffectingProjection() => ((ProjectionBase)this).Name = "Ledger";

        public int SideEffectsRaised { get; private set; }
        public List<FakeSession> Sessions { get; } = [];

        public override ValueTask<Ledger?> EvolveAsync(Ledger? snapshot, Guid id, FakeSession session, IEvent e,
            CancellationToken cancellation)
        {
            Sessions.Add(session);
            return ValueTask.FromResult<Ledger?>(snapshot ?? new Ledger { Id = id });
        }

        public override ValueTask RaiseSideEffects(FakeOperations operations, IEventSlice<Ledger> slice)
        {
            SideEffectsRaised++;
            return ValueTask.CompletedTask;
        }
    }

    public class BatchLedger
    {
        public int Id { get; set; }
        public int Balance { get; set; }
    }

    public class BatchProjection : JasperFxMultiStreamProjectionBase<BatchLedger, int, FakeOperations, FakeSession>
    {
        public BatchProjection()
        {
            ((ProjectionBase)this).Name = "Batches";
            Identity<Batch>(_ => 7);
            FanOut<Batch, Deposited>(x => x.Deposits);
        }

        public override BatchLedger? Evolve(BatchLedger? snapshot, int id, IEvent e)
        {
            snapshot ??= new BatchLedger { Id = id };

            if (e.Data is Deposited deposited)
            {
                snapshot.Balance += deposited.Amount;
            }

            return snapshot;
        }
    }
}
