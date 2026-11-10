using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Projections.ContainerScoped;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.Projections;

/// <summary>
/// jasperfx#1046 — the union of what Marten, Polecat and Fisher each tear down before a rebuild. A category
/// missing from a store fails silently: the progression row goes, the replay restarts from zero, and it lands
/// on the previous run's rows. So every category is pinned here, directly and through a composite's members.
/// </summary>
public class ProjectionTeardownPlanTests
{
    private static readonly string PublishedTableProjectionName = new PublishesATable().Name;

    private static ProjectionTeardownPlan planFor(IProjectionSource<FakeOperations, FakeSession> source)
        => ProjectionTeardownPlan.For(source);

    private static T assembled<T>() where T : ProjectionBase, new()
    {
        var projection = new T();
        projection.AssembleAndAssertValidity();
        return projection;
    }

    private static CompositeProjection<FakeOperations, FakeSession> compositeOf(
        params IProjectionSource<FakeOperations, FakeSession>[] members)
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        foreach (var member in members)
        {
            composite.StageFor(1).Add(member);
        }

        return composite;
    }

    // Published documents

    [Fact]
    public void an_aggregations_document_is_a_target()
    {
        planFor(new SingleStreamProjection<PlanDoc, Guid>()).DocumentTypes.ShouldBe([typeof(PlanDoc)]);
    }

    [Fact]
    public void an_event_projections_published_documents_are_targets()
    {
        planFor(assembled<CreatesTwoDocumentsProjection>()).DocumentTypes
            .ShouldBe([typeof(DocOne), typeof(DocTwo)], ignoreOrder: true);
    }

    [Fact]
    public void a_published_type_with_no_clean_up_rule_is_still_a_target()
    {
        // Marten reads only CleanUps, so this projection replays onto its old rows there
        var source = new CompositeProjectionSource<FakeOperations, FakeSession>(new PublishesWithoutARule());

        source.Options.CleanUps.ShouldBeEmpty();
        planFor(source).DocumentTypes.ShouldBe([typeof(DocOne)]);
    }

    [Fact]
    public void opting_out_of_published_type_teardown_is_honoured()
    {
        // jasperfx#626's append-only audit table. PublishedTypes() still lists the type, so sweeping it, as
        // Polecat and Fisher do, truncates exactly what the author excluded.
        var projection = assembled<AppendOnlyProjection>();

        projection.PublishedTypes().ShouldContain(typeof(DocOne));
        planFor(projection).DocumentTypes.ShouldBeEmpty();
    }

    [Fact]
    public void an_opt_out_still_runs_the_rules_the_author_declared()
    {
        var projection = assembled<AppendOnlyProjection>();
        projection.Options.DeleteViewTypeOnTeardown<DocTwo>();

        planFor(projection).DocumentTypes.ShouldBe([typeof(DocTwo)]);
    }

    [Fact]
    public void an_opt_out_is_honoured_through_a_wrapper()
    {
        // A wrapper is not the projection's type, but it adopts the options and copies the published types
        var services = new ServiceCollection();
        services.AddScoped<AppendOnlyProjection>();
        using var provider = services.BuildServiceProvider();

        var wrapper = new ScopedProjectionSourceWrapper<AppendOnlyProjection, FakeOperations, FakeSession>(provider);

        ((IProjectionSource<FakeOperations, FakeSession>)wrapper).PublishedTypes().ShouldContain(typeof(DocOne));
        planFor(wrapper).DocumentTypes.ShouldBeEmpty();
    }

    // Declared clean-ups

    [Fact]
    public void declared_clean_ups_are_targets()
    {
        var projection = assembled<CreatesDocumentsProjection>();
        projection.Options.DeleteViewTypeOnTeardown<DocTwo>();
        projection.Options.DeleteDataInTableOnTeardown("reporting.totals");

        var plan = planFor(projection);

        plan.DocumentTypes.ShouldBe([typeof(DocOne), typeof(DocTwo)], ignoreOrder: true);
        plan.Tables.ShouldBe(["reporting.totals"]);
    }

    [Fact]
    public void a_document_registered_both_ways_appears_once()
    {
        var projection = new SingleStreamProjection<PlanDoc, Guid>();
        projection.Options.DeleteViewTypeOnTeardown<PlanDoc>();

        planFor(projection).DocumentTypes.ShouldBe([typeof(PlanDoc)]);
    }

    // Flat tables

    [Fact]
    public void a_published_table_is_a_target()
    {
        planFor(assembled<PublishesATable>()).Tables.ShouldBe(["flat_table"]);
    }

    [Fact]
    public void a_published_table_is_not_lost_inside_a_wrapper()
    {
        // Fisher's check is `source is IPublishesTables`, which is false for every one of these
        var projection = new BarePublisher();
        var services = new ServiceCollection();
        services.AddScoped<BarePublisher>();
        services.AddScoped<PublishesATable>();
        using var provider = services.BuildServiceProvider();

        planFor(new ProjectionWrapper<FakeOperations, FakeSession>(projection, ProjectionLifecycle.Async))
            .Tables.ShouldBe(["bare_table"]);

        planFor(new CompositeProjectionSource<FakeOperations, FakeSession>(projection))
            .Tables.ShouldBe(["bare_table"]);

        planFor(new ScopedProjectionWrapper<BarePublisher, FakeOperations, FakeSession>(provider))
            .Tables.ShouldBe(["bare_table"]);

        planFor(new ScopedProjectionSourceWrapper<PublishesATable, FakeOperations, FakeSession>(provider))
            .Tables.ShouldBe(["flat_table"]);
    }

    [Fact]
    public void wrapping_a_published_table_more_than_once_does_not_repeat_the_rule()
    {
        var projection = new PublishesATableFromProjectionBase();

        _ = new ProjectionWrapper<FakeOperations, FakeSession>(projection, ProjectionLifecycle.Async);
        _ = new CompositeProjectionSource<FakeOperations, FakeSession>(projection);

        projection.Options.CleanUps.ShouldHaveSingleItem().ShouldBe(new DeleteTableData("base_table"));
    }

    // Natural keys

    [Fact]
    public void a_natural_key_aggregate_is_a_target()
    {
        planFor(new NkInstanceMethodProjection()).NaturalKeyAggregateTypes.ShouldBe([typeof(NkInstanceAggregate)]);
    }

    [Fact]
    public void an_aggregate_with_no_natural_key_contributes_none()
    {
        planFor(new SingleStreamProjection<PlanDoc, Guid>()).NaturalKeyAggregateTypes.ShouldBeEmpty();
    }

    // Composites

    [Fact]
    public void every_category_flows_up_from_a_composites_members()
    {
        var withClean = new SingleStreamProjection<PlanDoc, Guid>();
        withClean.Options.DeleteDataInTableOnTeardown("member_table");

        var plan = planFor(compositeOf(
            withClean,
            assembled<PublishesATable>(),
            new NkInstanceMethodProjection(),
            assembled<CreatesDocumentsProjection>()));

        plan.DocumentTypes.ShouldBe(
            [typeof(PlanDoc), typeof(NkInstanceAggregate), typeof(DocOne)], ignoreOrder: true);
        plan.Tables.ShouldBe(["member_table", "flat_table"], ignoreOrder: true);
        plan.NaturalKeyAggregateTypes.ShouldBe([typeof(NkInstanceAggregate)]);
    }

    [Fact]
    public void a_composites_own_rules_apply_alongside_its_members()
    {
        // marten#5175: the parent's options used to be consulted for nothing but its progression
        var composite = compositeOf(new SingleStreamProjection<PlanDoc, Guid>());
        composite.Options.DeleteViewTypeOnTeardown<DocTwo>();
        composite.Options.DeleteDataInTableOnTeardown("composite_table");

        var plan = planFor(composite);

        plan.DocumentTypes.ShouldBe([typeof(DocTwo), typeof(PlanDoc)], ignoreOrder: true);
        plan.Tables.ShouldBe(["composite_table"]);
    }

    [Fact]
    public void a_composite_does_not_reach_past_a_members_opt_out()
    {
        // composite.PublishedTypes() fans out to the members, so reading it is how Polecat wipes this
        var composite = compositeOf(assembled<AppendOnlyProjection>());

        ((IProjectionSource<FakeOperations, FakeSession>)composite).PublishedTypes().ShouldContain(typeof(DocOne));
        planFor(composite).DocumentTypes.ShouldBeEmpty();
    }

    [Fact]
    public void members_of_a_nested_composite_are_reached()
    {
        // Polecat's walk stopped one level down. ConstituentShardNames() has always gone all the way.
        var inner = new CompositeProjection<FakeOperations, FakeSession>("Inner");
        inner.StageFor(1).Add(new SingleStreamProjection<PlanDoc, Guid>());
        inner.Options.DeleteDataInTableOnTeardown("inner_table");

        var plan = planFor(compositeOf(inner));

        plan.DocumentTypes.ShouldBe([typeof(PlanDoc)]);
        plan.Tables.ShouldBe(["inner_table"]);
        plan.Shards.Select(x => x.Name).ShouldBe(["Composite", "Inner", "PlanDoc"], ignoreOrder: true);
    }

    [Fact]
    public void a_bare_projection_member_is_covered_by_what_was_declared_at_registration()
    {
        var bare = new CompositeProjectionSource<FakeOperations, FakeSession>(new BarePublisher());
        bare.Options.DeleteViewTypeOnTeardown<DocTwo>();

        planFor(compositeOf(bare)).DocumentTypes.ShouldBe([typeof(DocTwo)]);
    }

    // TeardownDataOnRebuild

    [Fact]
    public void turning_teardown_off_covers_every_data_category()
    {
        // No store honoured this everywhere: Marten and Polecat wipe the natural key table regardless,
        // Polecat wipes published types regardless, and Fisher never reads the flag at all
        var natural = new NkInstanceMethodProjection();
        natural.Options.DeleteDataInTableOnTeardown("declared");
        natural.Options.TeardownDataOnRebuild = false;

        var plan = planFor(natural);

        plan.DocumentTypes.ShouldBeEmpty();
        plan.Tables.ShouldBeEmpty();
        plan.NaturalKeyAggregateTypes.ShouldBeEmpty();

        var flat = assembled<PublishesATable>();
        flat.Options.TeardownDataOnRebuild = false;

        planFor(flat).Tables.ShouldBeEmpty();
    }

    [Fact]
    public void turning_teardown_off_keeps_the_progression_and_the_dead_letters()
    {
        // A rebuild restarts either way. Keeping the data is what the flag says, not keeping the position.
        var projection = new SingleStreamProjection<PlanDoc, Guid>();
        projection.Options.TeardownDataOnRebuild = false;

        var plan = planFor(projection);

        plan.Shards.ShouldHaveSingleItem().Identity.ShouldBe("PlanDoc:All");
        plan.DeadLetterProjectionNames.ShouldBe(["PlanDoc"]);
    }

    [Fact]
    public void a_member_can_turn_teardown_off_for_itself()
    {
        var kept = assembled<CreatesDocumentsProjection>();
        kept.Options.TeardownDataOnRebuild = false;

        planFor(compositeOf(kept, new SingleStreamProjection<PlanDoc, Guid>())).DocumentTypes
            .ShouldBe([typeof(PlanDoc)]);
    }

    [Fact]
    public void turning_teardown_off_on_a_composite_covers_its_members()
    {
        // In Marten and Polecat the parent's flag only silences the parent's own rules, so the members are
        // wiped anyway. A composite is rebuilt as a unit.
        var composite = compositeOf(new SingleStreamProjection<PlanDoc, Guid>(), assembled<PublishesATable>());
        composite.Options.TeardownDataOnRebuild = false;

        var plan = planFor(composite);

        plan.DocumentTypes.ShouldBeEmpty();
        plan.Tables.ShouldBeEmpty();
        plan.Shards.Select(x => x.Name).ShouldBe(["Composite", "PlanDoc", PublishedTableProjectionName], ignoreOrder: true);
    }

    // Progression and dead letters

    [Fact]
    public void the_shards_are_the_projections_own()
    {
        var projection = new SingleStreamProjection<PlanDoc, Guid>();
        projection.Version = 3;

        planFor(projection).Shards.ShouldHaveSingleItem().Identity.ShouldBe("PlanDoc:V3:All");
    }

    [Fact]
    public void a_composites_shards_include_every_member_under_the_name_its_execution_uses()
    {
        // fisher#402: a member row left at its old ceiling fails the replay's first batch as out of order.
        // Marten's and Polecat's per-tenant paths visit only the parent's shards.
        var member = new SingleStreamProjection<PlanDoc, Guid> { Version = 2 };
        var composite = compositeOf(member);

        var shards = planFor(composite).Shards;

        shards.Select(x => x.Identity).ShouldBe(["Composite:All", "PlanDoc:V2:All"]);
        shards[1].CompositeParentName.ShouldBe("Composite");
        shards[1].ForTenant("acme").Identity.ShouldBe("PlanDoc:V2:All:acme");
    }

    [Fact]
    public void dead_letters_are_cleared_for_a_composite_and_each_member()
    {
        planFor(compositeOf(new SingleStreamProjection<PlanDoc, Guid>(), assembled<PublishesATable>()))
            .DeadLetterProjectionNames
            .ShouldBe(["Composite", "PlanDoc", PublishedTableProjectionName], ignoreOrder: true);
    }

    [Fact]
    public void the_plan_says_which_projection_it_is_for()
    {
        planFor(compositeOf()).ProjectionName.ShouldBe("Composite");
    }

    // Resolving to the store's tables

    [Fact]
    public void every_table_category_resolves_through_the_store()
    {
        var projection = new NkInstanceMethodProjection();
        projection.Options.DeleteDataInTableOnTeardown("declared");

        var tables = planFor(projection).ResolveTables(new Tables());

        tables.ShouldBe(["doc_NkInstanceAggregate", "declared", "natural_NkInstanceAggregate"], ignoreOrder: true);
    }

    [Fact]
    public void a_store_can_point_a_document_type_at_an_external_providers_table()
    {
        // Fisher's storage providers: not a mapped document, but the provider registered where it writes
        var tables = planFor(new SingleStreamProjection<PlanDoc, Guid>())
            .ResolveTables(new Tables { [typeof(PlanDoc)] = "ef_core_table" });

        tables.ShouldBe(["ef_core_table"]);
    }

    [Fact]
    public void a_store_can_decline_a_document_type_it_does_not_own()
    {
        // marten#5329: an EF Core projection's table is not Marten's, and it declares its own clean-up
        var projection = new SingleStreamProjection<PlanDoc, Guid>();
        projection.Options.DeleteDataInTableOnTeardown("ef_core_table");

        var tables = planFor(projection).ResolveTables(new Tables { [typeof(PlanDoc)] = null });

        tables.ShouldBe(["ef_core_table"]);
    }

    [Fact]
    public void a_table_reached_two_ways_is_cleared_once()
    {
        var projection = new SingleStreamProjection<PlanDoc, Guid>();
        projection.Options.DeleteDataInTableOnTeardown("doc_PlanDoc");

        planFor(projection).ResolveTables(new Tables()).ShouldBe(["doc_PlanDoc"]);
    }

    private sealed class Tables : Dictionary<Type, string?>, IProjectionTableResolver
    {
        public string? DocumentTableFor(Type documentType)
            => TryGetValue(documentType, out var table) ? table : $"doc_{documentType.Name}";

        public string NaturalKeyTableFor(Type aggregateType) => $"natural_{aggregateType.Name}";
    }

    // The hand-written shape: a ProjectionBase that publishes a type and declares no rule
    private sealed class PublishesWithoutARule : ProjectionBase, IJasperFxProjection<FakeOperations>
    {
        public PublishesWithoutARule()
        {
            RegisterPublishedType(typeof(DocOne));
        }

        public Task ApplyAsync(FakeOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
            => Task.CompletedTask;
    }

    private sealed class BarePublisher : IJasperFxProjection<FakeOperations>, IPublishesTables
    {
        public Task ApplyAsync(FakeOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
            => Task.CompletedTask;

        public IEnumerable<string> PublishedTableNames() => ["bare_table"];
    }

    // Every wrapper around a ProjectionBase adopts its options, so several wrappers share one set
    private sealed class PublishesATableFromProjectionBase : ProjectionBase, IJasperFxProjection<FakeOperations>,
        IPublishesTables
    {
        public Task ApplyAsync(FakeOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
            => Task.CompletedTask;

        public IEnumerable<string> PublishedTableNames() => ["base_table"];
    }
}

public class PlanDoc
{
    public Guid Id { get; set; }
}

public partial class PublishesATable : EventProjection, IPublishesTables
{
    public void Project(AEvent e, FakeOperations ops)
    {
        // nothing
    }

    public IEnumerable<string> PublishedTableNames() => ["flat_table"];
}
