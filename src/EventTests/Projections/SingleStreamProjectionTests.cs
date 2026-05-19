using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class SingleStreamProjectionTests
{
    [Theory]
    [InlineData("Only using conventional methods", typeof(ConventionalProjection))]
    [InlineData("Overrides DetermineAction()", typeof(OverridesDetermineAction))]
    [InlineData("Overrides DetermineActionAsync()", typeof(OverridesDetermineActionAsync))]
    [InlineData("Overrides Evolve()", typeof(OverridesEvolve))]
    [InlineData("Overrides EvolveAsync()", typeof(OverridesEvolveAsync))]
    [InlineData("Overrides EnrichEventsAsync with conventional Apply", typeof(OverridesEnrichEventsAsyncWithConventionalApply))]
    public void validation_is_good_with_only_conventional_methods(string explanation, Type type)
    {
        Activator.CreateInstance(type).As<ProjectionBase>().AssembleAndAssertValidity();
    }

    // Regression for #298: tryUseAssemblyRegisteredEvolver was short-circuiting on
    // HasShouldDeleteMethods() BEFORE checking IGeneratedSyncDetermineAction, even though
    // that interface is exactly what the SG emits for self-aggregating docs with
    // ShouldDelete and handles the ShouldDelete arm internally. Result: registering
    // SingleStreamProjection<SelfAggregatingWithShouldDelete, Guid> threw
    // InvalidProjectionException at AssembleAndAssertValidity time.
    [Fact]
    public void self_aggregating_with_should_delete_binds_via_assembly_registered_determine_action_evolver()
    {
        var projection = new SingleStreamProjection<SelfAggregatingWithShouldDelete, Guid>();
        Should.NotThrow(() => projection.AssembleAndAssertValidity());
    }

    // Regression for #303: pre-#276 the reflection path wrapped each Apply call in a
    // try/catch keyed off RebuildErrors.SkipApplyErrors so the daemon could route just
    // the poison event to the dead-letter queue. The SG-emitted IGeneratedSyncDetermineAction
    // path processed the whole batch in one call, so a thrown Apply propagated as the raw
    // user exception with no per-event seam. The runtime adapter now dispatches one event
    // at a time and wraps each in ApplyEventException carrying *that* event's sequence,
    // restoring the seam.
    [Fact]
    public async Task sg_determine_action_wraps_per_event_apply_failure_in_apply_event_exception()
    {
        var projection = new SingleStreamProjection<SelfAggregatingWithFailingApply, Guid>();
        projection.AssembleAndAssertValidity();

        // Three events: the first is fine (Create), the second is the poison pill (Apply
        // throws), the third would otherwise succeed but execution stops at the poison.
        var events = new IEvent[]
        {
            new Event<AEvent>(new AEvent()) { Sequence = 1 },
            new Event<BEvent>(new BEvent()) { Sequence = 2 },  // poison
            new Event<AEvent>(new AEvent()) { Sequence = 3 }
        };

        var ex = await Should.ThrowAsync<ApplyEventException>(async () =>
        {
            await projection.DetermineActionAsync(
                new FakeSession(),
                null,
                Guid.NewGuid(),
                new NulloIdentitySetter<SelfAggregatingWithFailingApply, Guid>(),
                events,
                CancellationToken.None);
        });

        ex.Event.Sequence.ShouldBe(2);
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        ex.InnerException!.Message.ShouldBe("poison pill");
    }

    [Theory]
    [InlineData(typeof(ConventionalPlusEvolve), "This projection can only use the override of 'Evolve' or conventional Apply/Create/ShouldDelete methods, but not both")]
    [InlineData(typeof(MultipleOverrides), "Only one of these methods can be overridden: Evolve, EvolveAsync")]
    [InlineData(typeof(EmptyProjection), "No matching conventional Apply/Create/ShouldDelete methods for the EventTests.MyAggregate aggregate.")]
    public void validation_fails(Type type, string message)
    {
        var ex = Should.Throw<InvalidProjectionException>(() =>
        {
            Activator.CreateInstance(type).As<ProjectionBase>().AssembleAndAssertValidity();
        });
        
        ex.Message.ShouldBe(message);
    }

}

public class EmptyProjection : SingleStreamProjection<MyAggregate, Guid>
{
    
}

public class ConventionalPlusEvolve : SingleStreamProjection<MyAggregate, Guid>
{
    public void Apply(AEvent e, MyAggregate a) => a.ACount++;

    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        return base.Evolve(snapshot, id, e);
    }
}

public class MultipleOverrides : SingleStreamProjection<MyAggregate, Guid>
{
    public override ValueTask<MyAggregate?> EvolveAsync(MyAggregate? snapshot, Guid id, FakeSession session, IEvent e, CancellationToken cancellation)
    {
        return base.EvolveAsync(snapshot, id, session, e, cancellation);
    }

    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        return base.Evolve(snapshot, id, e);
    }
}

public partial class ConventionalProjection : SingleStreamProjection<MyAggregate, Guid>
{
    public void Apply(AEvent e, MyAggregate a) => a.ACount++;
}

public class OverridesDetermineAction : SingleStreamProjection<MyAggregate, Guid>
{
    public override (MyAggregate?, ActionType) DetermineAction(MyAggregate? snapshot, Guid identity, IReadOnlyList<IEvent> events)
    {
        throw new NotImplementedException();
    }
}

public class OverridesDetermineActionAsync : SingleStreamProjection<MyAggregate, Guid>
{
    public override ValueTask<(MyAggregate?, ActionType)> DetermineActionAsync(FakeSession session, MyAggregate? snapshot, Guid identity,
        IIdentitySetter<MyAggregate, Guid> identitySetter, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        throw new NotImplementedException();
    }
}

public class OverridesEvolve : SingleStreamProjection<MyAggregate, Guid>
{
    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        throw new NotImplementedException();
    }
}

public class OverridesEvolveAsync : SingleStreamProjection<MyAggregate, Guid>
{
    public override ValueTask<MyAggregate?> EvolveAsync(MyAggregate? snapshot, Guid id, FakeSession session, IEvent e, CancellationToken cancellation)
    {
        throw new NotImplementedException();
    }
}

public partial class OverridesEnrichEventsAsyncWithConventionalApply : SingleStreamProjection<MyAggregate, Guid>
{
    public override Task EnrichEventsAsync(
        SliceGroup<MyAggregate, Guid> group, FakeSession session, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public void Apply(AEvent e, MyAggregate a) => a.ACount++;
}

// Self-aggregating fixture for #298 regression. Apply + Create + ShouldDelete on the
// document type → SG emits IGeneratedSyncDetermineAction<SelfAggregatingWithShouldDelete, Guid>
// (with the ShouldDelete arm baked into the switch) + [assembly: GeneratedEvolver(...)].
public partial class SelfAggregatingWithShouldDelete
{
    public Guid Id { get; set; }
    public int ACount { get; set; }

    public static SelfAggregatingWithShouldDelete Create(AEvent _) => new();
    public void Apply(BEvent _) => ACount++;
    public bool ShouldDelete(CEvent _) => true;
}

// Self-aggregating fixture for #303 regression. Apply on BEvent throws — the daemon
// rebuild flow needs the runtime to re-raise that as ApplyEventException carrying the
// BEvent so SkipApplyErrors can dead-letter just that event. ShouldDelete is present
// so the SG emits IGeneratedSyncDetermineAction (the path the issue targets).
public partial class SelfAggregatingWithFailingApply
{
    public Guid Id { get; set; }

    public static SelfAggregatingWithFailingApply Create(AEvent _) => new();
    public void Apply(BEvent _) => throw new InvalidOperationException("poison pill");
    public bool ShouldDelete(CEvent _) => true;
}

public class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, FakeOperations, FakeSession> where TDoc : notnull where TId : notnull
{
    public SingleStreamProjection() : base()
    {
    }
}

public class FakeSingleProjectionStream<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, FakeOperations, FakeSession> where TDoc : notnull where TId : notnull
{
    public FakeSingleProjectionStream(Type[] transientExceptionTypes) : base()
    {
    }
}

