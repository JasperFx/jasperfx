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
        // 'explanation' names the case; xUnit1026 flagged it as unused. Feeding it to Should.NotThrow
        // keeps it as the label it was always meant to be, and now it shows up in the failure message.
        Should.NotThrow(
            () => Activator.CreateInstance(type).As<ProjectionBase>().AssembleAndAssertValidity(),
            explanation);
    }

    // jasperfx#778 put Archived into a single stream projection's event types; jasperfx#796 is the
    // same defect one type over. Every store composes its async loader's filter from
    // IncludedEventTypes, so a Compacted<TDoc> missing here is a filtered shard that never sees the
    // marker -- and the marker is the only thing left of the pre-compaction history, because
    // CompactStreamAsync deletes the events it folds. The shard then folds post-compaction events
    // onto a null snapshot and persists an aggregate missing everything before the compaction, with
    // nothing thrown and nothing logged.
    [Fact]
    public void declared_event_types_carry_archived_and_compacted_over_the_aggregate()
    {
        var projection = new ConventionalProjection();
        projection.AssembleAndAssertValidity();

        projection.AllEventTypes.ShouldContain(typeof(Archived));
        projection.AllEventTypes.ShouldContain(typeof(Compacted<MyAggregate>));
    }

    // Compacted<T> is closed over the projection's OWN aggregate. Appending an open or a
    // wrong-aggregate marker would widen the shard's filter to markers it has nothing to do with,
    // which is the same objection that keeps Archived out of the multi stream scope.
    [Fact]
    public void the_compacted_marker_is_closed_over_this_projections_aggregate_only()
    {
        var projection = new ConventionalProjection();
        projection.AssembleAndAssertValidity();

        projection.AllEventTypes.ShouldNotContain(typeof(Compacted<>));
        projection.AllEventTypes.ShouldNotContain(typeof(Compacted<MyOtherAggregate>));
    }

    // AssembleAndAssertValidity ends with IncludedEventTypes.Fill(determineEventTypes()), so the
    // marker this override appends is written back into IncludedEventTypes -- and the next evaluation
    // concats that list again, past the base's own Distinct(), and appends the marker a second time.
    // Before jasperfx#796 this shipped: AllEventTypes came back [AEvent, Archived, Archived]. It does
    // not change what AppliesTo answers, but every store composes its async loader's allow list
    // straight from these types, so the duplicate reaches the generated SQL as a repeated IN member
    // and a repeated parameter slot.
    [Fact]
    public void the_appended_markers_are_not_duplicated_by_the_assembly_fill()
    {
        var projection = new ConventionalProjection();

        // The Fill happens here -- one call is enough to seed IncludedEventTypes with the markers.
        projection.AssembleAndAssertValidity();

        projection.AllEventTypes.ShouldBe(projection.AllEventTypes.Distinct().ToArray());
    }

    // The empty-set guard from jasperfx#778, restated for the second marker. AppliesTo reads an
    // empty AllEventTypes as "applies to everything" -- how a catch-all Evolve(IEvent) projection
    // declares itself -- so appending either marker unconditionally turns that "everything" into
    // "exactly two types" and the projection then sees nothing at all.
    [Fact]
    public void an_empty_event_type_set_stays_empty()
    {
        var projection = new CatchAllEvolveProjection();
        projection.AssembleAndAssertValidity();

        projection.AllEventTypes.ShouldBeEmpty();
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

    // Regression for #305: PR #304 wrapped the runtime adapter delegates but the
    // *projection subclass* path bypasses those — when a partial projection has
    // Apply + ShouldDelete, the SG emits a `DetermineActionAsync` override directly
    // on the user class, so establishBuildActionAndEvolve sets
    // `_buildAction = DetermineActionAsync` (the SG-emitted method) and the
    // per-event wrap in tryUseAssemblyRegisteredEvolver is bypassed entirely.
    // The Marten DaemonTests rebuild_the_projection_skip_failed_events failure was
    // raw InvalidOperationException leaking up through the SG override directly to
    // GroupedProjectionExecution.buildBatchWithSkipping, which only routes events
    // for AggregateException-of-ApplyEventException. The SG-emitted DetermineActionAsync
    // must wrap each event's switch body itself.
    [Fact]
    public async Task sg_determine_action_async_override_on_partial_projection_wraps_per_event()
    {
        var projection = new ProjectionWithFailingApplyAndShouldDelete();
        projection.AssembleAndAssertValidity();

        var events = new IEvent[]
        {
            new Event<AEvent>(new AEvent()) { Sequence = 10 },     // Create, fine
            new Event<BEvent>(new BEvent()) { Sequence = 11 },     // poison Apply
            new Event<AEvent>(new AEvent()) { Sequence = 12 }
        };

        var ex = await Should.ThrowAsync<ApplyEventException>(async () =>
        {
            await projection.DetermineActionAsync(
                new FakeSession(),
                null,
                Guid.NewGuid(),
                new NulloIdentitySetter<MyAggregate, Guid>(),
                events,
                CancellationToken.None);
        });

        ex.Event.Sequence.ShouldBe(11);
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        ex.InnerException!.Message.ShouldBe("You shall not pass!");
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

// Regression fixture for #305. Apply on BEvent throws. The SG sees ShouldDelete +
// Apply on a partial projection subclass → emits a DetermineActionAsync override
// directly on this class. That override's per-event switch body must now wrap the
// thrown user exception as ApplyEventException carrying *that* event so the
// daemon's buildBatchWithSkipping can route the poison event to the dead-letter queue.
public partial class ProjectionWithFailingApplyAndShouldDelete : SingleStreamProjection<MyAggregate, Guid>
{
    public static MyAggregate Create(AEvent _) => new();
    public void Apply(BEvent _, MyAggregate _2) => throw new InvalidOperationException("You shall not pass!");
    public bool ShouldDelete(CEvent _) => false;
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

// A catch-all projection: it declares no event types at all, so AllEventTypes must come back empty
// and AppliesTo must keep reading that as "applies to everything". Fixture for the empty-set guard.
public class CatchAllEvolveProjection : SingleStreamProjection<MyAggregate, Guid>
{
    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        return snapshot ?? new MyAggregate();
    }
}

// Only ever used as a type argument, to prove the appended Compacted<T> is closed over the
// projection's own aggregate rather than over anything else.
public class MyOtherAggregate
{
    public Guid Id { get; set; }
}
