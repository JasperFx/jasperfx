using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

// #653: dispatch for a projection whose conventional methods live on the instance now runs on the
// projection the store registered. The evolver used to build its own shadow — `new TProjection()`, or
// GetUninitializedObject when there was no public parameterless constructor — so a dependency handed
// in by the container was null (marten#4787) and anything the real constructor set was missing.
public partial class DiActivatedProjection : SingleStreamProjection<MyAggregate, Guid>
{
    private readonly ICounterSink _sink;

    public DiActivatedProjection(ICounterSink sink) => _sink = sink;

    public void Apply(AEvent e, MyAggregate a)
    {
        // Would NRE on a shadow instance built without running this constructor.
        _sink.Record();
        a.ACount++;
    }
}

public interface ICounterSink
{
    void Record();
}

public class CounterSink : ICounterSink
{
    public int Count { get; private set; }
    public void Record() => Count++;
}

// A public parameterless constructor was no reason to dispatch through a different instance either:
// the evolver used to hold `new StatefulProjection()`, so anything the conventional method recorded
// landed on a shadow the caller never sees.
public partial class StatefulProjection : SingleStreamProjection<MyAggregate, Guid>
{
    public int Handled { get; private set; }

    public void Apply(AEvent e, MyAggregate a)
    {
        Handled++;
        a.ACount++;
    }
}

public class projection_instance_binding
{
    [Fact]
    public async Task dispatch_runs_on_the_registered_di_built_instance()
    {
        var sink = new CounterSink();
        var projection = new DiActivatedProjection(sink);
        projection.AssembleAndAssertValidity();

        var (snapshot, action) = await projection.DetermineActionAsync(
            new FakeSession(),
            new MyAggregate(),
            Guid.NewGuid(),
            new NulloIdentitySetter<MyAggregate, Guid>(),
            [new Event<AEvent>(new AEvent()) { Sequence = 1 }],
            CancellationToken.None);

        // The injected dependency was reachable from the conventional method...
        sink.Count.ShouldBe(1);
        // ...and the event was actually applied.
        action.ShouldBe(ActionType.Store);
        snapshot!.ACount.ShouldBe(1);
    }

    [Fact]
    public async Task a_projection_with_a_parameterless_constructor_dispatches_on_itself_too()
    {
        var projection = new StatefulProjection();
        projection.AssembleAndAssertValidity();

        await projection.DetermineActionAsync(
            new FakeSession(),
            new MyAggregate(),
            Guid.NewGuid(),
            new NulloIdentitySetter<MyAggregate, Guid>(),
            [new Event<AEvent>(new AEvent()) { Sequence = 1 }],
            CancellationToken.None);

        // Fails against a shadow instance: the increment would land on the evolver's own copy.
        projection.Handled.ShouldBe(1);
    }
}
