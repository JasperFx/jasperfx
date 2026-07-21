using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

// Coverage for jasperfx#544: the step-instrumented aggregation fold on the shared
// base. Drives the REAL slice -> group -> enrich -> Create/Apply/ShouldDelete path
// (no reflection re-implementation) and captures a per-event before/after timeline
// for every resulting identity, streaming each step to an observer.
public class StepInstrumentedFoldTests
{
    private static readonly Func<object?, JsonElement?> Serialize =
        state => state is null ? null : JsonSerializer.SerializeToElement(state);

    private static EventRecord ToRecord(IEvent e) => new(
        e.Id,
        e.Sequence,
        e.Version,
        e.StreamId.ToString(),
        e.EventType.Name,
        JsonSerializer.SerializeToElement(e.Data),
        null,
        e.Timestamp,
        e.TenantId,
        null);

    private static IEvent A(Guid stream, long seq, long version) =>
        new Event<AEvent>(new AEvent()) { StreamId = stream, Sequence = seq, Version = version };

    private static IEvent B(Guid stream, long seq, long version) =>
        new Event<BEvent>(new BEvent()) { StreamId = stream, Sequence = seq, Version = version };

    [Fact]
    public async Task single_identity_produces_exactly_one_timeline_with_per_event_state()
    {
        var projection = new SteppingProjection();
        projection.AssembleAndAssertValidity();

        var stream = Guid.NewGuid();
        var events = new[] { A(stream, 1, 1), A(stream, 2, 2), B(stream, 3, 3) };

        var observer = new CollectingObserver();

        var result = await projection.BuildTimelinesAsync(
            events, new FakeSession(), Serialize, ToRecord, observer, CancellationToken.None);

        result.ProjectionName.ShouldBe(projection.Name);
        // Done-when: a single-identity aggregate projection produces exactly one timeline.
        result.AggregatesByIdentity.Count.ShouldBe(1);

        var timeline = result.AggregatesByIdentity[stream.ToString()];
        timeline.Steps.Count.ShouldBe(3);

        // Before of the very first step is null (no prior state); after of the first step exists.
        timeline.Steps[0].Before.ShouldBeNull();
        timeline.Steps[0].After.ShouldNotBeNull();

        // Per-event before/after walk: A -> ACount 1, A -> ACount 2, B -> BCount 1.
        StateOf(timeline.Steps[1].Before!.Value).ACount.ShouldBe(1);
        StateOf(timeline.Steps[1].After!.Value).ACount.ShouldBe(2);
        StateOf(timeline.Steps[2].After!.Value).BCount.ShouldBe(1);

        var final = StateOf(timeline.FinalState!.Value);
        final.ACount.ShouldBe(2);
        final.BCount.ShouldBe(1);

        // The observer saw every step, in apply order, tagged with the identity.
        observer.Steps.Count.ShouldBe(3);
        observer.Steps.ShouldAllBe(x => x.Identity == stream.ToString());
    }

    [Fact]
    public async Task fans_a_single_event_list_out_across_multiple_identities()
    {
        var projection = new SteppingProjection();
        projection.AssembleAndAssertValidity();

        var stream1 = Guid.NewGuid();
        var stream2 = Guid.NewGuid();

        // One flat event list spanning two streams -> two timelines.
        var events = new[]
        {
            A(stream1, 1, 1),
            B(stream2, 2, 1),
            A(stream1, 3, 2)
        };

        var result = await projection.BuildTimelinesAsync(
            events, new FakeSession(), Serialize, ToRecord, null, CancellationToken.None);

        result.AggregatesByIdentity.Count.ShouldBe(2);

        StateOf(result.AggregatesByIdentity[stream1.ToString()].FinalState!.Value).ACount.ShouldBe(2);
        StateOf(result.AggregatesByIdentity[stream2.ToString()].FinalState!.Value).BCount.ShouldBe(1);
    }

    [Fact]
    public async Task captures_apply_errors_on_the_step_and_keeps_folding()
    {
        var projection = new ThrowsOnBProjection();
        projection.AssembleAndAssertValidity();

        var stream = Guid.NewGuid();
        var events = new[] { A(stream, 1, 1), B(stream, 2, 2), A(stream, 3, 3) };

        var result = await projection.BuildTimelinesAsync(
            events, new FakeSession(), Serialize, ToRecord, null, CancellationToken.None);

        var timeline = result.AggregatesByIdentity[stream.ToString()];
        timeline.Steps.Count.ShouldBe(3);

        // The poison B event records its error but does not abort the run...
        timeline.Steps[1].Error.ShouldNotBeNull();
        timeline.Steps[1].Error!.ShouldContain("no B allowed");

        // ...and the surrounding A events still fold, so ACount reaches 2.
        StateOf(timeline.FinalState!.Value).ACount.ShouldBe(2);
    }

    private static MyAggregate StateOf(JsonElement element) =>
        element.Deserialize<MyAggregate>()!;

    private sealed class CollectingObserver : IProjectionStepObserver
    {
        public List<(string Identity, ProjectionStepResultRaw Step)> Steps { get; } = new();

        public ValueTask ObserveAsync(string identity, ProjectionStepResultRaw step, CancellationToken cancellation)
        {
            Steps.Add((identity, step));
            return ValueTask.CompletedTask;
        }
    }

    // Overrides Evolve directly so the fold is exercised without depending on the source generator.
    private sealed class SteppingProjection : SingleStreamProjection<MyAggregate, Guid>
    {
        public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
        {
            snapshot ??= new MyAggregate { Id = id };
            switch (e.Data)
            {
                case AEvent:
                    snapshot.ACount++;
                    break;
                case BEvent:
                    snapshot.BCount++;
                    break;
            }

            return snapshot;
        }
    }

    private sealed class ThrowsOnBProjection : SingleStreamProjection<MyAggregate, Guid>
    {
        public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
        {
            snapshot ??= new MyAggregate { Id = id };
            switch (e.Data)
            {
                case AEvent:
                    snapshot.ACount++;
                    break;
                case BEvent:
                    throw new InvalidOperationException("no B allowed");
            }

            return snapshot;
        }
    }
}
