using EventTests.Projections;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.ContainerScoped;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace EventTests.Descriptors;

/// <summary>
/// ProjectionSourceWrapperBase resolves a new instance of the projection to describe it, and that instance
/// has never heard of anything registration did. Name, Version and Lifecycle land on the wrapper
/// (<c>wrapper.Lifecycle = lifecycle; configure?.Invoke(wrapper);</c> in Marten and Fisher alike), and the
/// wrapper is also what runs, so a console was being told about a projection and a shard that do not exist.
/// </summary>
public class ProjectionSourceWrapperDescriptionTests
{
    private static readonly FakeEventStore theStore = new();

    private static ServiceProvider servicesWith<T>() where T : class
        => new ServiceCollection().AddScoped<T>().BuildServiceProvider();

    private static ProjectionBase scopedConventionalProjection(IServiceProvider services)
        => ScopedAggregationWrapper.Build(services, typeof(ConventionalProjection), typeof(MyAggregate), typeof(Guid),
            typeof(FakeOperations), typeof(FakeSession));

    private static SubscriptionDescriptor describe(ProjectionBase wrapper)
        => ((ISubscriptionSource)wrapper).Describe(theStore);

    [Fact]
    public void reports_the_lifecycle_it_was_registered_with()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        wrapper.Lifecycle = ProjectionLifecycle.Inline;

        describe(wrapper).Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
    }

    [Fact]
    public void reports_the_lifecycle_the_graph_gave_it()
    {
        using var services = servicesWith<ConventionalProjection>();
        var graph = new FakeGraph();
        graph.Register((IProjectionSource<FakeOperations, FakeSession>)scopedConventionalProjection(services),
            ProjectionLifecycle.Inline);

        var usage = new EventStoreUsage(new Uri("fake://store"), theStore);
        graph.Describe(usage, theStore);

        usage.Subscriptions.Single().Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
    }

    [Fact]
    public void reports_the_name_it_was_registered_with()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        wrapper.Name = "RenamedAtRegistration";

        describe(wrapper).Name.ShouldBe("RenamedAtRegistration");
    }

    [Fact]
    public void reports_the_version_it_was_registered_with()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        wrapper.Version = 3;

        describe(wrapper).Version.ShouldBe(3u);
    }

    /// <summary>
    /// The shard name is what a consumer correlates with a progression row and an agent URI, so a stale one
    /// points at nothing. Compared to Shards(), which is what the daemon actually runs.
    /// </summary>
    [Fact]
    public void reports_the_shards_that_actually_run()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        wrapper.Name = "RenamedAtRegistration";
        wrapper.Version = 3;

        var running = ((ISubscriptionSource<FakeOperations, FakeSession>)wrapper).Shards().Select(x => x.Name.Identity);

        describe(wrapper).ShardNames.Select(x => x.Identity).ShouldBe(running);
    }

    [Fact]
    public void an_inline_registration_has_no_daemon_metrics_or_spans()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        wrapper.Lifecycle = ProjectionLifecycle.Inline;

        var descriptor = describe(wrapper);

        descriptor.Metrics.ShouldBeEmpty();
        descriptor.ActivitySpans.ShouldBeEmpty();
    }

    [Fact]
    public void async_metrics_and_spans_follow_the_registered_shard()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        wrapper.Name = "RenamedAtRegistration";

        var descriptor = describe(wrapper);
        var expected = new SubscriptionDescriptor((ISubscriptionSource)wrapper, theStore);

        expected.Metrics.ShouldNotBeEmpty();
        descriptor.Metrics.Select(x => x.Name).ShouldBe(expected.Metrics.Select(x => x.Name));
        descriptor.ActivitySpans.Select(x => x.Name).ShouldBe(expected.ActivitySpans.Select(x => x.Name));
    }

    [Fact]
    public void a_projection_that_declares_itself_inline_but_was_registered_async_reports_async()
    {
        using var services = servicesWith<DeclaresItselfInlineProjection>();
        var wrapper = new ScopedProjectionSourceWrapper<DeclaresItselfInlineProjection, FakeOperations, FakeSession>(services);

        wrapper.Lifecycle = ProjectionLifecycle.Async;

        var descriptor = describe(wrapper);

        descriptor.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
        descriptor.Metrics.ShouldNotBeEmpty();
    }

    [Fact]
    public void reports_an_event_type_included_at_registration()
    {
        using var services = servicesWith<CreatesDocumentsProjection>();
        var wrapper = new ScopedProjectionSourceWrapper<CreatesDocumentsProjection, FakeOperations, FakeSession>(services);

        wrapper.IncludeType<BEvent>();

        describe(wrapper).AppliedEvents.Select(x => x.FullName).ShouldContain(typeof(BEvent).FullName);
    }

    [Fact]
    public void a_scoped_aggregation_reports_the_same_events_as_the_projection_it_wraps()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        var unwrapped = new ConventionalProjection().Describe(theStore);

        describe(wrapper).AppliedEvents.Select(x => x.FullName)
            .ShouldBe(unwrapped.AppliedEvents.Select(x => x.FullName), ignoreOrder: true);
    }

    [Fact]
    public void keeps_what_only_the_projection_can_say_about_itself()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = scopedConventionalProjection(services);

        wrapper.Name = "RenamedAtRegistration";

        var descriptor = describe(wrapper);

        descriptor.Subject.ShouldBe(typeof(ConventionalProjection).FullName);
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.SingleStreamProjection);
        descriptor.ImplementationType.ShouldBe(TypeDescriptor.For(typeof(ConventionalProjection)));
        descriptor.AggregateType.ShouldBe(TypeDescriptor.For(typeof(MyAggregate)));
        descriptor.IdentityType.ShouldBe(TypeDescriptor.For(typeof(Guid)));
    }

    /// <summary>Describe() is virtual on an event projection, so a projection can add to its description</summary>
    [Fact]
    public void keeps_what_a_projection_added_to_its_own_description()
    {
        using var services = servicesWith<AddsToItsDescriptionProjection>();
        var wrapper = new ScopedProjectionSourceWrapper<AddsToItsDescriptionProjection, FakeOperations, FakeSession>(services);

        wrapper.Name = "RenamedAtRegistration";

        describe(wrapper).Properties.ShouldContain(x => x.Subject.EndsWith("Added"));
    }

    /// <summary>
    /// Not a bug, and pinned so it stays that way: this wrapper is the event projection path Marten and Fisher
    /// use, and it describes itself, so it has always reported what was registered on it.
    /// </summary>
    [Fact]
    public void the_scoped_projection_wrapper_already_reports_what_was_registered()
    {
        using var services = servicesWith<CreatesDocumentsProjection>();
        var wrapper = new ScopedProjectionWrapper<CreatesDocumentsProjection, FakeOperations, FakeSession>(services);

        ((ProjectionBase)wrapper).Name = "RenamedAtRegistration";
        wrapper.Lifecycle = ProjectionLifecycle.Inline;

        var descriptor = wrapper.Describe(theStore);

        descriptor.Name.ShouldBe("RenamedAtRegistration");
        descriptor.Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
        descriptor.Metrics.ShouldBeEmpty();
    }

    private sealed class FakeGraph : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
        }
    }
}

public partial class DeclaresItselfInlineProjection : EventProjection
{
    public DeclaresItselfInlineProjection()
    {
        Lifecycle = ProjectionLifecycle.Inline;
    }

    public DocOne Create(AEvent e) => new();
}

public partial class AddsToItsDescriptionProjection : EventProjection
{
    public DocOne Create(AEvent e) => new();

    public override SubscriptionDescriptor Describe(IEventStore store)
    {
        var descriptor = base.Describe(store);
        descriptor.AddValue("Added", "by the projection");
        return descriptor;
    }
}
