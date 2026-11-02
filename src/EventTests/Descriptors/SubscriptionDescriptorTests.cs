using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EventTests.Projections;
using EventTests.Subscriptions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Projections.ContainerScoped;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Descriptors;

public class SubscriptionDescriptorTests
{
    
    
    [Fact]
    public void fill_from_subject()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.Subscription);
        source.Name.Returns("Foo");
        source.Version.Returns(2U);
        source.ShardNames().Returns([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);
        source.ImplementationType.Returns(typeof(FakeSubscription));

        var store = new FakeEventStore();

        var descriptor = new SubscriptionDescriptor(source, store);
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.Subscription);
        descriptor.Name.ShouldBe("Foo");
        descriptor.Version.ShouldBe(2U);
        descriptor.Lifecycle.ShouldBe(ProjectionLifecycle.Async);

        descriptor.ImplementationType.ShouldBe(TypeDescriptor.For(typeof(FakeSubscription)));
        descriptor.AggregateType.ShouldBeNull();

        descriptor.ShardNames.ShouldBe([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);

        descriptor.Metrics.Select(x => x.Name).ShouldBe(["fake.foo.one.gap", "fake.foo.two.gap"]);
        descriptor.Metrics.Select(x => x.Type).Distinct().Single().ShouldBe(MetricsType.Histogram);
        
        descriptor.ActivitySpans.Select(x => x.Name).ShouldBe(["fake.foo.one.page.execution", "fake.foo.one.page.loading", "fake.foo.one.page.grouping", "fake.foo.two.page.execution", "fake.foo.two.page.loading", "fake.foo.two.page.grouping"]);
    }

    [Fact]
    public void serializable()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.Subscription);
        source.Name.Returns("Foo");
        source.Version.Returns(2U);
        source.ShardNames().Returns([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);
        source.ImplementationType.Returns(typeof(FakeSubscription));

        var store = new FakeEventStore();

        var descriptor = new SubscriptionDescriptor(source, store);

        descriptor.ShouldBeSerializable();
    }

    [Fact]
    public void tolerates_a_source_with_no_implementation_type()
    {
        // ISubscriptionSource.ImplementationType is declared non-nullable, but the
        // descriptor is diagnostics-only metadata — a source that returns null (as an
        // unconfigured mock does) should yield a null ImplementationType, not an NRE
        // out of the whole Describe() pipeline. Pins the jasperfx#465 regression.
        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.Subscription);
        source.Name.Returns("Foo");
        source.Version.Returns(2U);
        source.ShardNames().Returns([new ShardName("Foo", "One", 2U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);

        var descriptor = new SubscriptionDescriptor(source, new FakeEventStore());

        descriptor.ImplementationType.ShouldBeNull();
        descriptor.AggregateType.ShouldBeNull();
    }

    [Fact]
    public void descriptor_for_inline_should_have_no_metrics_or_activities()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.Subscription);
        source.Name.Returns("Foo");
        source.Version.Returns(2U);
        source.ShardNames().Returns([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Inline);

        var store = new FakeEventStore();

        var descriptor = new SubscriptionDescriptor(source, store);
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.Subscription);
        descriptor.Name.ShouldBe("Foo");
        descriptor.Version.ShouldBe(2U);
        descriptor.Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
        
        descriptor.ShardNames.ShouldBe([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);

        descriptor.Metrics.Any().ShouldBeFalse();
        descriptor.ActivitySpans.Any().ShouldBeFalse();
    }
}

/// <summary>
/// jasperfx#1102: the descriptor carries what a projection writes, for every kind of projection and
/// through every wrapper. The wrappers are the point: what a store describes is usually the wrapper rather
/// than the projection the user wrote, and one that does not forward reads exactly like a projection that
/// writes nothing.
/// </summary>
public class SubscriptionDescriptorPublishedTypesTests
{
    private static readonly FakeEventStore theStore = new();

    private static string[] publishedBy(SubscriptionDescriptor descriptor)
        => descriptor.PublishedTypes.Select(x => x.FullName).ToArray();

    private static string[] namesOf(params Type[] types) => types.Select(x => x.FullName!).ToArray();

    private static ServiceProvider servicesWith<T>() where T : class
        => new ServiceCollection().AddScoped<T>().BuildServiceProvider();

    // ---- every kind of projection, unwrapped ----

    [Fact]
    public void a_single_stream_projection_publishes_its_document()
    {
        var descriptor = new ConventionalProjection().Describe(theStore);

        publishedBy(descriptor).ShouldBe(namesOf(typeof(MyAggregate)));

        // AggregateType is not retired by this: it means "the aggregate", not "what is written"
        descriptor.AggregateType.ShouldBe(TypeDescriptor.For(typeof(MyAggregate)));
    }

    [Fact]
    public void a_multi_stream_projection_publishes_its_document()
    {
        var descriptor = new DayProjection().Describe(theStore);

        publishedBy(descriptor).ShouldBe(namesOf(typeof(Day)));
        descriptor.AggregateType.ShouldBe(TypeDescriptor.For(typeof(Day)));
    }

    [Fact]
    public void an_event_projection_publishes_every_document_it_creates()
    {
        var descriptor = new CreatesTwoDocumentsProjection().Describe(theStore);

        publishedBy(descriptor).ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
        descriptor.AggregateType.ShouldBeNull();
    }

    [Fact]
    public void a_composite_flattens_its_members_across_stages()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Flattened");
        composite.StageFor(1).Add(new ConventionalProjection());
        composite.StageFor(2).Add(new CreatesTwoDocumentsProjection());

        var descriptor = ((ISubscriptionSource)composite).Describe(theStore);

        // By full name, not by stage: EventTests.MyAggregate sorts ahead of EventTests.Projections.DocOne
        publishedBy(descriptor).ShouldBe(namesOf(typeof(MyAggregate), typeof(DocOne), typeof(DocTwo)));
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.CompositeProjection);
        descriptor.AggregateType.ShouldBeNull();
    }

    [Fact]
    public void a_nested_composite_is_flattened_too()
    {
        var inner = new CompositeProjection<FakeOperations, FakeSession>("Inner");
        inner.StageFor(1).Add(new CreatesTwoDocumentsProjection());

        var outer = new CompositeProjection<FakeOperations, FakeSession>("Outer");
        outer.StageFor(1).Add(inner);
        outer.StageFor(2).Add(new ConventionalProjection());

        publishedBy(((ISubscriptionSource)outer).Describe(theStore))
            .ShouldBe(namesOf(typeof(MyAggregate), typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void a_composite_reports_what_it_declares_itself()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("OwnTeardown");
        composite.StageFor(1).Add(new CreatesTwoDocumentsProjection());
        composite.Options.DeleteViewTypeOnTeardown<Day>();

        publishedBy(((ISubscriptionSource)composite).Describe(theStore))
            .ShouldBe(namesOf(typeof(Day), typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void a_composite_with_no_members_still_reports_what_it_declares_itself()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("OwnTeardown");
        composite.Options.DeleteViewTypeOnTeardown<Day>();

        composite.PublishedTypes().ShouldBe([typeof(Day)]);
    }

    /// <summary>
    /// Polecat and Fisher read the first published type of a projection as its state type, so what the
    /// composite declares for itself goes after its members rather than ahead of them.
    /// </summary>
    [Fact]
    public void a_composite_lists_its_members_ahead_of_itself()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("OwnTeardown");
        composite.StageFor(1).Add(new CreatesTwoDocumentsProjection());
        composite.Options.DeleteViewTypeOnTeardown<Day>();

        composite.PublishedTypes().ShouldBe([typeof(DocOne), typeof(DocTwo), typeof(Day)]);
    }

    [Fact]
    public void a_type_a_composite_and_its_member_both_name_is_listed_once()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("OwnTeardown");
        composite.StageFor(1).Add(new CreatesTwoDocumentsProjection());
        composite.Options.DeleteViewTypeOnTeardown<DocOne>();

        composite.PublishedTypes().ShouldBe([typeof(DocOne), typeof(DocTwo)]);
    }

    [Fact]
    public void a_nested_composite_keeps_what_it_declares_itself()
    {
        var inner = new CompositeProjection<FakeOperations, FakeSession>("Inner");
        inner.Options.DeleteViewTypeOnTeardown<Day>();

        var outer = new CompositeProjection<FakeOperations, FakeSession>("Outer");
        outer.StageFor(1).Add(inner);

        outer.PublishedTypes().ShouldBe([typeof(Day)]);
    }

    [Fact]
    public void a_composite_subclass_can_register_a_published_type()
    {
        var composite = new CompositeRegisteringItsOwnType();

        composite.PublishedTypes().ShouldBe([typeof(Day)]);
    }

    [Fact]
    public void two_composite_members_writing_one_document_report_it_once()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Shared");
        composite.StageFor(1).Add(new CreatesTwoDocumentsProjection());
        composite.StageFor(2).Add(new CreatesDocumentsProjection());

        publishedBy(((ISubscriptionSource)composite).Describe(theStore))
            .ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
    }

    // ---- through each wrapper ----

    [Fact]
    public void projection_wrapper_forwards_a_projection_base()
    {
        var wrapper = new ProjectionWrapper<FakeOperations, FakeSession>(new DocumentWritingProjection(),
            ProjectionLifecycle.Inline);

        publishedBy(wrapper.Describe(theStore)).ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void projection_wrapper_reports_what_was_declared_for_a_bare_projection_at_registration()
    {
        // A bare IJasperFxProjection declares nothing itself, so all there can be is the teardown rule
        // that registration attached to the wrapper's own options
        var wrapper = new ProjectionWrapper<FakeOperations, FakeSession>(new BareFakeProjection(),
            ProjectionLifecycle.Inline);

        publishedBy(wrapper.Describe(theStore)).ShouldBeEmpty();

        wrapper.Options.DeleteViewTypeOnTeardown<DocTwo>();

        publishedBy(wrapper.Describe(theStore)).ShouldBe(namesOf(typeof(DocTwo)));
    }

    [Fact]
    public void composite_projection_source_forwards_a_projection_base()
    {
        var source = new CompositeProjectionSource<FakeOperations, FakeSession>(new DocumentWritingProjection());

        publishedBy(source.Describe(theStore)).ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void scoped_projection_wrapper_forwards_the_projection_it_resolves()
    {
        using var services = servicesWith<CreatesTwoDocumentsProjection>();
        var wrapper =
            new ScopedProjectionWrapper<CreatesTwoDocumentsProjection, FakeOperations, FakeSession>(services);

        publishedBy(wrapper.Describe(theStore)).ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void scoped_projection_source_wrapper_forwards_the_source_it_resolves()
    {
        using var services = servicesWith<CreatesTwoDocumentsProjection>();
        ISubscriptionSource wrapper =
            new ScopedProjectionSourceWrapper<CreatesTwoDocumentsProjection, FakeOperations, FakeSession>(services);

        publishedBy(wrapper.Describe(theStore)).ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void scoped_single_stream_wrapper_forwards_its_document()
    {
        using var services = servicesWith<ConventionalProjection>();
        var wrapper = (ISubscriptionSource)ScopedAggregationWrapper.Build(services, typeof(ConventionalProjection),
            typeof(MyAggregate), typeof(Guid), typeof(FakeOperations), typeof(FakeSession));

        var descriptor = wrapper.Describe(theStore);

        publishedBy(descriptor).ShouldBe(namesOf(typeof(MyAggregate)));
        descriptor.AggregateType.ShouldBe(TypeDescriptor.For(typeof(MyAggregate)));
    }

    [Fact]
    public void scoped_multi_stream_wrapper_forwards_its_document()
    {
        using var services = servicesWith<DayProjection>();
        var wrapper = (ISubscriptionSource)ScopedAggregationWrapper.Build(services, typeof(DayProjection),
            typeof(Day), typeof(int), typeof(FakeOperations), typeof(FakeSession));

        publishedBy(wrapper.Describe(theStore)).ShouldBe(namesOf(typeof(Day)));
    }

    /// <summary>
    /// ProjectionSourceWrapperBase kept its copy of the source's types in a private field behind an
    /// explicit interface implementation, and ProjectionBase's public PublishedTypes() -- which is what a
    /// non-generic interface binds to on any class deriving from it -- never saw them. Assembly usually hides
    /// that, by copying them into the shared Options as teardown targets. Opting out of teardown does not.
    /// </summary>
    [Fact]
    public void a_scoped_source_wrapper_read_as_its_own_subject_is_not_empty()
    {
        using var services = servicesWith<AppendOnlyProjection>();
        var wrapper = new ScopedProjectionSourceWrapper<AppendOnlyProjection, FakeOperations, FakeSession>(services);

        publishedBy(new SubscriptionDescriptor(wrapper, theStore)).ShouldBe(namesOf(typeof(DocOne)));
        publishedBy(((ISubscriptionSource)wrapper).Describe(theStore)).ShouldBe(namesOf(typeof(DocOne)));

        // ...and the two ways to ask a wrapper cannot disagree. Marten's composite adopts a scoped member's
        // types through the ProjectionBase-typed spelling.
        ((ProjectionBase)wrapper).PublishedTypes().ShouldBe([typeof(DocOne)]);
        ((IProjectionSource<FakeOperations, FakeSession>)wrapper).PublishedTypes().ShouldBe([typeof(DocOne)]);
    }

    [Fact]
    public void opting_out_of_teardown_is_not_opting_out_of_being_described()
    {
        publishedBy(new AppendOnlyProjection().Describe(theStore)).ShouldBe(namesOf(typeof(DocOne)));
    }

    /// <summary>
    /// Describe() on a source wrapper resolves a NEW instance of the projection, which has never heard of
    /// the Options that registration configured on the wrapper.
    /// </summary>
    [Fact]
    public void a_scoped_source_wrapper_reports_what_was_declared_on_it_at_registration()
    {
        using var services = servicesWith<CreatesTwoDocumentsProjection>();
        var wrapper =
            new ScopedProjectionSourceWrapper<CreatesTwoDocumentsProjection, FakeOperations, FakeSession>(services);

        wrapper.Options.DeleteViewTypeOnTeardown<Day>();

        publishedBy(((ISubscriptionSource)wrapper).Describe(theStore))
            .ShouldBe(namesOf(typeof(Day), typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void a_composite_flattens_a_scoped_member()
    {
        using var services = servicesWith<CreatesTwoDocumentsProjection>();

        var composite = new CompositeProjection<FakeOperations, FakeSession>("Scoped");
        composite.StageFor(1)
            .Add(new ScopedProjectionSourceWrapper<CreatesTwoDocumentsProjection, FakeOperations, FakeSession>(services));

        publishedBy(((ISubscriptionSource)composite).Describe(theStore))
            .ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
    }

    [Fact]
    public void a_source_that_does_not_derive_from_projection_base_is_reached_too()
    {
        // Implemented explicitly, so nothing but the interface's own forwarding can find it
        publishedBy(new SubscriptionDescriptor(new HandWrittenSource(), theStore)).ShouldBe(namesOf(typeof(DocOne)));
    }

    // ---- subscriptions, which write nothing ----

    [Fact]
    public void a_plain_subscription_publishes_nothing()
    {
        var descriptor = new FakeSubscriptionSource().Describe(theStore);

        descriptor.PublishedTypes.ShouldNotBeNull();
        descriptor.PublishedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_subscription_wrapper_publishes_nothing()
    {
        var wrapper = new SubscriptionWrapper<FakeOperations, FakeSession, IFakeSubscription>(new FakeSubscriptionSource());

        wrapper.Describe(theStore).PublishedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_scoped_subscription_wrapper_publishes_nothing()
    {
        using var services = servicesWith<FakeSubscriptionSource>();
        var wrapper =
            new ScopedSubscriptionServiceWrapper<FakeSubscriptionSource, FakeOperations, FakeSession, IFakeSubscription>(services);

        wrapper.Describe(theStore).PublishedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_source_that_is_no_projection_at_all_publishes_nothing()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.ShardNames().Returns([new ShardName("Foo")]);

        new SubscriptionDescriptor(source, theStore).PublishedTypes.ShouldBeEmpty();
    }

    // ---- through the graph, which is the path every store takes ----

    [Fact]
    public void the_graph_describes_each_kind_with_what_it_writes()
    {
        var graph = new FakeGraph();
        graph.Register(new ConventionalProjection(), ProjectionLifecycle.Inline);
        graph.Register(new CreatesTwoDocumentsProjection(), ProjectionLifecycle.Inline);
        graph.Add(new DocumentWritingProjection(), ProjectionLifecycle.Inline);

        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        composite.StageFor(1).Add(new DayProjection());
        graph.Register(composite, ProjectionLifecycle.Async);

        graph.Subscribe(new FakeSubscriptionSource());

        var usage = new EventStoreUsage(new Uri("fake://store"), theStore);
        graph.Describe(usage, theStore);

        var published = usage.Subscriptions.ToDictionary(x => x.ImplementationType!.FullName, publishedBy);

        published[typeof(FakeSubscriptionSource).FullName!].ShouldBeEmpty();
        published[typeof(ConventionalProjection).FullName!].ShouldBe(namesOf(typeof(MyAggregate)));
        published[typeof(CreatesTwoDocumentsProjection).FullName!].ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
        published[typeof(DocumentWritingProjection).FullName!].ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
        published[typeof(CompositeProjection<FakeOperations, FakeSession>).FullName!].ShouldBe(namesOf(typeof(Day)));
    }

    // ---- distinct and stable ----

    [Fact]
    public void the_order_does_not_follow_the_order_of_registration()
    {
        var forward = new ProjectionWrapper<FakeOperations, FakeSession>(
            new RegistersInOrder(typeof(DocOne), typeof(DocTwo), typeof(Day)), ProjectionLifecycle.Inline);
        var backward = new ProjectionWrapper<FakeOperations, FakeSession>(
            new RegistersInOrder(typeof(Day), typeof(DocTwo), typeof(DocOne)), ProjectionLifecycle.Inline);

        publishedBy(backward.Describe(theStore)).ShouldBe(publishedBy(forward.Describe(theStore)));
    }

    [Fact]
    public void a_type_named_by_several_routes_is_reported_once()
    {
        var projection = new RegistersInOrder(typeof(DocOne), typeof(DocOne));
        projection.Options.DeleteViewTypeOnTeardown<DocOne>();

        publishedBy(new ProjectionWrapper<FakeOperations, FakeSession>(projection, ProjectionLifecycle.Inline)
            .Describe(theStore)).ShouldBe(namesOf(typeof(DocOne)));
    }

    // ---- reading it can neither fail nor change anything ----

    [Fact]
    public void describing_an_unassembled_projection_reports_its_documents_and_leaves_it_alone()
    {
        var projection = new CreatesTwoDocumentsProjection();

        publishedBy(projection.Describe(theStore)).ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));

        projection.Options.StorageTypes.ShouldBeEmpty();
        projection.Options.CleanUps.ShouldBeEmpty();

        // The answer does not move once the projection is assembled either
        projection.AssembleAndAssertValidity();
        publishedBy(projection.Describe(theStore)).ShouldBe(namesOf(typeof(DocOne), typeof(DocTwo)));
    }

    [Theory]
    [InlineData(typeof(CreateReturnsNothingProjection))]
    [InlineData(typeof(CreateReturnsTaskProjection))]
    [InlineData(typeof(CreateReturnsValueTaskProjection))]
    public void a_create_method_with_nothing_to_store_is_not_reported_as_a_document(Type projectionType)
    {
        var projection = (EventProjection)Activator.CreateInstance(projectionType)!;

        new SubscriptionDescriptor(projection, theStore).PublishedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_projection_that_failed_validation_can_still_be_described()
    {
        var projection = new CreateReturnsNothingProjection();

        Should.Throw<InvalidProjectionException>(() => projection.AssembleAndAssertValidity());

        new SubscriptionDescriptor(projection, theStore).PublishedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_projection_whose_published_types_throw_does_not_take_describe_down_with_it()
    {
        var descriptor = new SubscriptionDescriptor(new ThrowsOnPublishedTypes(), theStore);

        descriptor.Name.ShouldBe(nameof(ThrowsOnPublishedTypes));
        descriptor.PublishedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_projection_that_returns_null_reads_as_empty()
    {
        new SubscriptionDescriptor(new HandWrittenSource(returnNull: true), theStore).PublishedTypes.ShouldBeEmpty();
    }

    // ---- on the wire ----

    private static SubscriptionDescriptor anEventProjectionDescriptor() =>
        new CreatesTwoDocumentsProjection().Describe(theStore);

    [Fact]
    public void round_trips_through_json()
    {
        var descriptor = anEventProjectionDescriptor();

        var json = JsonSerializer.Serialize(descriptor);
        var read = JsonSerializer.Deserialize<SubscriptionDescriptor>(json)!;

        read.PublishedTypes.ShouldBe(descriptor.PublishedTypes);
        read.PublishedTypes.ShouldNotBeEmpty();
    }

    [Fact]
    public void round_trips_through_camel_case_json()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var descriptor = anEventProjectionDescriptor();

        var json = JsonSerializer.Serialize(descriptor, options);

        JsonNode.Parse(json)!["publishedTypes"]!.AsArray().Count.ShouldBe(2);
        JsonSerializer.Deserialize<SubscriptionDescriptor>(json, options)!.PublishedTypes
            .ShouldBe(descriptor.PublishedTypes);
    }

    [Fact]
    public void a_payload_from_before_the_field_existed_reads_as_empty()
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(anEventProjectionDescriptor()))!.AsObject();
        payload.Remove(nameof(SubscriptionDescriptor.PublishedTypes)).ShouldBeTrue();

        var read = JsonSerializer.Deserialize<SubscriptionDescriptor>(payload.ToJsonString())!;

        read.PublishedTypes.ShouldNotBeNull();
        read.PublishedTypes.ShouldBeEmpty();
        read.Name.ShouldBe(typeof(CreatesTwoDocumentsProjection).FullNameInCode());
    }

    [Fact]
    public void a_payload_with_a_null_field_reads_as_empty()
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(anEventProjectionDescriptor()))!.AsObject();
        payload[nameof(SubscriptionDescriptor.PublishedTypes)] = null;

        var read = JsonSerializer.Deserialize<SubscriptionDescriptor>(payload.ToJsonString())!;

        read.PublishedTypes.ShouldNotBeNull();
        read.PublishedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_reader_that_predates_the_field_ignores_it()
    {
        var read = JsonSerializer.Deserialize<ReaderFromBeforeTheField>(
            JsonSerializer.Serialize(new ConventionalProjection().Describe(theStore)))!;

        read.AggregateType!.FullName.ShouldBe(typeof(MyAggregate).FullName);
    }

    [Fact]
    public void a_descriptor_built_for_deserialization_starts_empty()
    {
        new SubscriptionDescriptor(SubscriptionType.EventProjection).PublishedTypes.ShouldBeEmpty();
    }

    public record ReaderFromBeforeTheField(string Name, TypeDescriptor? AggregateType);

    // ---- the projections ----

    public class CompositeRegisteringItsOwnType : CompositeProjection<FakeOperations, FakeSession>
    {
        public CompositeRegisteringItsOwnType() : base("Registering")
        {
            RegisterPublishedType(typeof(Day));
        }
    }

    public class BareFakeProjection : IJasperFxProjection<FakeOperations>
    {
        public Task ApplyAsync(FakeOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
            => Task.CompletedTask;
    }

    /// <summary>A hand-written projection, which only ever reaches a graph through a ProjectionWrapper.</summary>
    public class DocumentWritingProjection : ProjectionBase, IJasperFxProjection<FakeOperations>
    {
        public DocumentWritingProjection()
        {
            RegisterPublishedType(typeof(DocTwo));
            RegisterPublishedType(typeof(DocOne));
        }

        public Task ApplyAsync(FakeOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
            => Task.CompletedTask;
    }

    public class RegistersInOrder : ProjectionBase, IJasperFxProjection<FakeOperations>
    {
        public RegistersInOrder(params Type[] types)
        {
            foreach (var type in types) RegisterPublishedType(type);
        }

        public Task ApplyAsync(FakeOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
            => Task.CompletedTask;
    }

    public class CreateReturnsNothingProjection : EventProjection
    {
        public void Create(AEvent e)
        {
        }
    }

    public class CreateReturnsTaskProjection : EventProjection
    {
        public Task Create(AEvent e) => Task.CompletedTask;
    }

    public class CreateReturnsValueTaskProjection : EventProjection
    {
        public ValueTask Create(AEvent e) => ValueTask.CompletedTask;
    }

    public class ThrowsOnPublishedTypes : EventProjection
    {
        public ThrowsOnPublishedTypes()
        {
            Name = nameof(ThrowsOnPublishedTypes);
        }

        public override IEnumerable<Type> PublishedTypes()
            => throw new InvalidOperationException("Not until the store has built");
    }

    /// <summary>
    /// An <see cref="IProjectionSource{TOperations,TQuerySession}" /> that owes nothing to ProjectionBase, and
    /// answers only through an explicit implementation.
    /// </summary>
    private class HandWrittenSource(bool returnNull = false) : IProjectionSource<FakeOperations, FakeSession>
    {
        IEnumerable<Type> IProjectionSource<FakeOperations, FakeSession>.PublishedTypes()
            => returnNull ? null! : [typeof(DocOne)];

        public string Name => nameof(HandWrittenSource);
        public uint Version => 1;
        public SubscriptionType Type => SubscriptionType.EventProjection;
        public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;
        public ShardName[] ShardNames() => [ShardName.Compose(Name)];
        public Type ImplementationType => GetType();
        public SubscriptionDescriptor Describe(IEventStore store) => new(this, store);
        public IReadOnlyList<AsyncShard<FakeOperations, FakeSession>> Shards() => [];
        public AsyncOptions Options { get; } = new();

        public bool TryBuildReplayExecutor(IEventStore<FakeOperations, FakeSession> store, IEventDatabase database,
            [NotNullWhen(true)] out IReplayExecutor? executor)
        {
            executor = null;
            return false;
        }

        public IInlineProjection<FakeOperations> BuildForInline() => throw new NotSupportedException();
    }

    private sealed class FakeGraph : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        public void Subscribe(ISubscriptionSource<FakeOperations, FakeSession> subscription)
            => registerSubscription(subscription);

        protected override void onAddProjection(object projection)
        {
        }
    }
}

public class FakeSubscription
{
}

public class FakeEventStore : IEventStore
{
    public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Uri Subject { get; } = "store://fake".ToUri();
    public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
    {
        throw new NotImplementedException();
    }

    public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
    {
        throw new NotImplementedException();
    }

    public EventStoreIdentity Identity => new("fake", "fake");
    public IReadOnlyEventStore OpenReadOnlyEventStore()
    {
        throw new NotImplementedException();
    }

    public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public Meter Meter { get; } = new Meter("Fake");
    public ActivitySource ActivitySource { get; } = new ActivitySource("Fake");
    public string MetricsPrefix { get; } = "fake";
    public DatabaseCardinality DatabaseCardinality { get; set; } = DatabaseCardinality.Single; 
    public bool HasMultipleTenants { get; set; }
}