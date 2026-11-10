#if NET11_0_OR_GREATER
using System.Reflection;
using System.Text.Json;
using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Shouldly;

namespace EventTests.Unions;

// jasperfx#1053: C# 15 unions as events. A union is stored as its active case (STJ writes the bare case
// anyway, and only the event type name can tell the cases apart on the way back), and an Apply method
// taking the union handles every case of it. See spikes/StjUnionSpike for the measurements.
public class UnionTypesTests
{
    [Fact]
    public void keyword_and_hand_written_unions_are_unions()
    {
        typeof(AccountEvent).IsUnion().ShouldBeTrue();
        typeof(LedgerEvent).IsUnion().ShouldBeTrue();

        typeof(UnionDeposited).IsUnion().ShouldBeFalse();
        typeof(string).IsUnion().ShouldBeFalse();
    }

    [Fact]
    public void case_types_come_from_the_case_constructors()
    {
        typeof(AccountEvent).CaseTypes()
            .ShouldBe([typeof(UnionDeposited), typeof(UnionWithdrawn), typeof(UnionFrozen)], ignoreOrder: true);

        typeof(LedgerEvent).CaseTypes()
            .ShouldBe([typeof(UnionDeposited), typeof(UnionWithdrawn)], ignoreOrder: true);

        typeof(UnionDeposited).CaseTypes().ShouldBeEmpty();
    }

    [Fact]
    public void expanding_a_nested_union_reaches_the_innermost_cases()
    {
        typeof(AccountLifecycle).ExpandToCaseTypes()
            .ShouldBe([typeof(UnionOpened), typeof(UnionDeposited), typeof(UnionWithdrawn), typeof(UnionFrozen)],
                ignoreOrder: true);

        typeof(UnionDeposited).ExpandToCaseTypes().ShouldBe([typeof(UnionDeposited)]);
    }

    [Fact]
    public void union_cases_through_system_text_json_agree_with_reflection()
    {
        var cases = typeof(AccountEvent).UnionCasesFor(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        cases.Select(x => x.CaseType)
            .ShouldBe(typeof(AccountEvent).CaseTypes(), ignoreOrder: true);
        cases.ShouldAllBe(x => !x.IsNullable);

        typeof(UnionDeposited).UnionCasesFor(new JsonSerializerOptions()).ShouldBeEmpty();
    }

    [Fact]
    public void unwrap_returns_the_active_case_through_nested_unions()
    {
        var deposited = new UnionDeposited(5m);

        UnionTypes.Unwrap(new AccountEvent(deposited)).ShouldBeSameAs(deposited);
        UnionTypes.Unwrap(new AccountLifecycle(new AccountEvent(deposited))).ShouldBeSameAs(deposited);
        UnionTypes.Unwrap(new LedgerEvent(deposited)).ShouldBeSameAs(deposited);

        // Not a union: handed back untouched
        UnionTypes.Unwrap(deposited).ShouldBeSameAs(deposited);
    }

    [Fact]
    public void unwrapping_a_default_union_is_refused()
    {
        var ex = Should.Throw<InvalidOperationException>(() => UnionTypes.Unwrap(default(AccountEvent)));
        ex.Message.ShouldContain("no active case");
    }

    [Fact]
    public void wrap_rebuilds_the_union_from_a_case()
    {
        var deposited = new UnionDeposited(5m);

        var wrapped = UnionTypes.Wrap(typeof(AccountEvent), deposited);
        wrapped.ShouldBeOfType<AccountEvent>();
        ((AccountEvent)wrapped).Value.ShouldBeSameAs(deposited);

        UnionTypes.Wrap(typeof(UnionDeposited), deposited).ShouldBeSameAs(deposited);
    }
}

public class union_events_on_append
{
    [Fact]
    public void build_event_stores_the_active_case_under_its_own_name()
    {
        var registry = new EventRegistry();
        var withdrawn = new UnionWithdrawn(3m, "rent");

        var e = registry.BuildEvent(new AccountEvent(withdrawn));

        e.Data.ShouldBeSameAs(withdrawn);
        e.EventType.ShouldBe(typeof(UnionWithdrawn));
        e.EventTypeName.ShouldBe("union_withdrawn");
        e.ShouldBeAssignableTo<IEvent<UnionWithdrawn>>();
    }

    [Fact]
    public void build_event_unwraps_hand_written_and_nested_unions()
    {
        var registry = new EventRegistry();

        registry.BuildEvent(new LedgerEvent(new UnionDeposited(1m))).EventType.ShouldBe(typeof(UnionDeposited));
        registry.BuildEvent(new AccountLifecycle(new AccountEvent(new UnionFrozen("ops"))))
            .EventType.ShouldBe(typeof(UnionFrozen));
        registry.BuildEvent(new AccountLifecycle(new UnionOpened("me")))
            .EventType.ShouldBe(typeof(UnionOpened));
    }

    [Fact]
    public void the_stored_json_is_unchanged_by_unwrapping()
    {
        // STJ already writes a union as its bare case, so unwrapping changes the event type, not the data.
        var union = new AccountEvent(new UnionWithdrawn(3m, "rent"));
        var e = new EventRegistry().BuildEvent(union);

        JsonSerializer.Serialize(e.Data, e.EventType).ShouldBe(JsonSerializer.Serialize(union));
    }

    [Fact]
    public void a_default_union_cannot_be_appended()
    {
        Should.Throw<InvalidOperationException>(() => new EventRegistry().BuildEvent(default(AccountEvent)));
    }

    [Fact]
    public void stream_append_unwraps_each_union()
    {
        var registry = new EventRegistry();

        var action = StreamAction.Append(registry, Guid.NewGuid(),
            new AccountEvent(new UnionDeposited(1m)), new UnionFrozen("ops"));

        action.Events.Select(x => x.EventType).ShouldBe([typeof(UnionDeposited), typeof(UnionFrozen)]);
    }

    [Fact]
    public void a_union_raised_from_a_slice_is_stored_as_its_case()
    {
        var streamId = Guid.NewGuid();
        var slice = new EventSlice<UnionAccount, Guid>(streamId, "tenant");
        IEventSlice<UnionAccount> raising = slice;

        raising.AppendEvent(new AccountEvent(new UnionDeposited(1m)));
        raising.AppendEvent(Guid.NewGuid(), new AccountEvent(new UnionFrozen("ops")));
        raising.AppendEvent(new UnionWithdrawn(1m, "fee"));

        var raised = raising.RaisedEvents().ToList();
        raised.Select(x => x.EventType)
            .ShouldBe([typeof(UnionDeposited), typeof(UnionFrozen), typeof(UnionWithdrawn)]);
        raised[0].StreamId.ShouldBe(streamId);
    }

    [Fact]
    public void registering_a_union_registers_its_cases()
    {
        var registry = new EventRegistry();
        registry.AddEventType(typeof(AccountEvent));

        registry.EventMappingFor(typeof(UnionDeposited)).EventTypeName.ShouldBe("union_deposited");
        registry.EventMappingFor(typeof(UnionWithdrawn)).EventTypeName.ShouldBe("union_withdrawn");
        registry.EventMappingFor(typeof(UnionFrozen)).EventTypeName.ShouldBe("union_frozen");
    }
}

public class apply_union_method_discovery
{
    [Fact]
    public void apply_union_on_the_aggregate_handles_every_case()
    {
        var evolver = EvolverFor<UnionAccount>();
        var snapshot = new UnionAccount();

        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionDeposited(10m)))!;
        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new AccountEvent(new UnionWithdrawn(4m, "fee"))))!;
        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionFrozen("ops")))!;

        snapshot.Balance.ShouldBe(6m);
        snapshot.FrozenBy.ShouldBe("ops");
        snapshot.Handled.ShouldBe(["union:deposited", "union:withdrawn", "union:frozen"]);
    }

    [Fact]
    public void the_generated_event_types_are_the_cases_not_the_union()
    {
        var evolver = EvolverFor<UnionAccount>();

        evolver.EventTypes.ShouldBe([typeof(UnionDeposited), typeof(UnionWithdrawn), typeof(UnionFrozen)],
            ignoreOrder: true);
    }

    [Fact]
    public void an_explicit_case_handler_wins_over_apply_union()
    {
        var evolver = EvolverFor<PrecedenceAccount>();
        var snapshot = new PrecedenceAccount();

        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionDeposited(1m)))!;
        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionFrozen("ops")))!;

        snapshot.Handled.ShouldBe(["union:UnionDeposited", "explicit:ops"]);
    }

    [Fact]
    public void apply_with_a_hand_written_union()
    {
        var evolver = EvolverFor<LedgerAccount>();
        var snapshot = new LedgerAccount();

        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionDeposited(10m)))!;
        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionWithdrawn(3m, "fee")))!;

        snapshot.Balance.ShouldBe(7m);
    }

    [Fact]
    public void apply_with_a_nested_union_reaches_the_inner_cases()
    {
        var evolver = EvolverFor<LifecycleAccount>();
        var snapshot = new LifecycleAccount();

        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionOpened("me")))!;
        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionDeposited(10m)))!;
        snapshot = evolver.Evolve(snapshot, snapshot.Id, Build(new UnionWithdrawn(4m, "fee")))!;

        snapshot.Owner.ShouldBe("me");
        snapshot.Balance.ShouldBe(6m);

        evolver.EventTypes.ShouldBe(
            [typeof(UnionOpened), typeof(UnionDeposited), typeof(UnionWithdrawn), typeof(UnionFrozen)],
            ignoreOrder: true);
    }

    [Fact]
    public void a_self_aggregating_snapshot_projection_listens_for_the_cases()
    {
        var projection = new SingleStreamProjection<UnionAccount, Guid>();
        projection.AssembleAndAssertValidity();

        projection.AllEventTypes.ShouldContain(typeof(UnionDeposited));
        projection.AllEventTypes.ShouldContain(typeof(UnionWithdrawn));
        projection.AllEventTypes.ShouldContain(typeof(UnionFrozen));

        // Never stored under the union's own name, so a filter on it would match nothing
        projection.AllEventTypes.ShouldNotContain(typeof(AccountEvent));
        projection.IncludedEventTypes.ShouldNotContain(typeof(AccountEvent));
    }

    [Fact]
    public void apply_union_on_a_projection_class_is_valid_and_listens_for_the_cases()
    {
        var projection = new ProjectedAccountProjection();
        projection.AssembleAndAssertValidity();

        projection.AllEventTypes.ShouldContain(typeof(UnionDeposited));
        projection.AllEventTypes.ShouldContain(typeof(UnionWithdrawn));
        projection.AllEventTypes.ShouldContain(typeof(UnionFrozen));
        projection.AllEventTypes.ShouldNotContain(typeof(AccountEvent));
    }

    [Fact]
    public void apply_union_on_a_projection_class_evolves_each_case()
    {
        var projection = new ProjectedAccountProjection();
        var attribute = typeof(apply_union_method_discovery).Assembly
            .GetCustomAttributes<GeneratedEvolverAttribute>()
            .Single(x => x.ProjectionType == typeof(ProjectedAccountProjection));
        var evolver = (IGeneratedSyncEvolver<ProjectedAccount, Guid>)Activator.CreateInstance(attribute.EvolverType, projection)!;

        var id = Guid.NewGuid();
        var snapshot = new ProjectedAccount { Id = id };

        snapshot = evolver.Evolve(snapshot, id, Build(new UnionDeposited(10m)))!;
        snapshot = evolver.Evolve(snapshot, id, Build(new AccountEvent(new UnionWithdrawn(4m, "fee"))))!;

        snapshot.Balance.ShouldBe(6m);
    }

    [Theory]
    [InlineData(typeof(CreateFromUnionProjection))]
    [InlineData(typeof(WrappedUnionApplyProjection))]
    public void a_union_anywhere_but_a_bare_apply_parameter_is_refused(Type projectionType)
    {
        var projection = (JasperFx.Events.Projections.ProjectionBase)Activator.CreateInstance(projectionType)!;

        var ex = Should.Throw<JasperFx.Events.Projections.InvalidProjectionException>(
            () => projection.AssembleAndAssertValidity());

        ex.Message.ShouldContain("only supported as the bare event parameter of an Apply method");
    }

    private static IEvent Build(object data) => new EventRegistry().BuildEvent(data);

    private static IGeneratedSyncEvolver<T, Guid> EvolverFor<T>() where T : notnull
    {
        var attribute = typeof(apply_union_method_discovery).Assembly
            .GetCustomAttributes<GeneratedEvolverAttribute>()
            .SingleOrDefault(x => x.AggregateType == typeof(T) && x.ProjectionType == null);

        attribute.ShouldNotBeNull($"the source generator emitted no evolver for {typeof(T).Name}");

        return (IGeneratedSyncEvolver<T, Guid>)Activator.CreateInstance(attribute.EvolverType)!;
    }
}
#endif
