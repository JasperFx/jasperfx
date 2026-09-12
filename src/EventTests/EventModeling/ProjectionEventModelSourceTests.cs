using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.EventModeling;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#825 — the store-derived rung: one View slice per registered projection, read out of the
/// event store's own registry.
/// </summary>
/// <remarks>
/// Bobcat declares, Wolverine derives from chains, CritterWatch observes — and nothing derived from
/// the store, so a View slice appeared on a canvas only when a human wrote one down. Everything here
/// runs through <see cref="IEventStore.TryCreateUsage" /> and <see cref="SubscriptionDescriptor" />,
/// which every store fills through shared code, so no store needs its own copy.
/// </remarks>
public class ProjectionEventModelSourceTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static SubscriptionDescriptor projection(
        SubscriptionType type = SubscriptionType.SingleStreamProjection,
        params Type[] applied)
        => new(type)
        {
            Name = nameof(AccountBalance),
            ImplementationType = T<BalanceProjection>(),
            AggregateType = T<AccountBalance>(),
            AppliedEvents = applied.Select(TypeDescriptor.For).ToArray(),
            ShardNames = [],
        };

    private static IEventStore storeWith(params SubscriptionDescriptor[] subscriptions)
    {
        var usage = new EventStoreUsage { SubjectUri = new Uri("store://test") };
        usage.Subscriptions.AddRange(subscriptions);

        var store = Substitute.For<IEventStore>();
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(usage));
        return store;
    }

    /// <summary>
    /// The acceptance shape: a single-stream projection becomes one View slice carrying the
    /// projection, its document, and every applied event.
    /// </summary>
    [Fact]
    public void a_single_stream_projection_becomes_a_view_slice()
    {
        var slice = ProjectionEventModelSource
            .ToSlice(projection(applied: [typeof(AccountOpened), typeof(MoneyDeposited)]))
            .ShouldNotBeNull();

        slice.Name.ShouldBe(nameof(AccountBalance));
        slice.Pattern.ShouldBe(SlicePattern.View);
        slice.ProjectionTypes.ShouldHaveSingleItem().ShouldBe(T<BalanceProjection>());
        slice.ReadModelTypes.ShouldHaveSingleItem().ShouldBe(T<AccountBalance>());
        slice.ConsumedEvents.ShouldBe([T<AccountOpened>(), T<MoneyDeposited>()]);
    }

    /// <summary>
    /// The slice is named after the <em>document</em>, not the projection class.
    /// </summary>
    /// <remarks>
    /// Deliberate rather than convenient: Bobcat's <c>{readmodel}</c> capture and the curated model
    /// file already name View slices this way, so the two rungs merge by name into one slice instead
    /// of two stickies that say the same thing. Naming it <c>BalanceProjection</c> would break exactly
    /// that.
    /// </remarks>
    [Fact]
    public void the_slice_is_named_after_the_document_not_the_projection()
    {
        ProjectionEventModelSource.ToSlice(projection())!.Name.ShouldBe(nameof(AccountBalance));
        ProjectionEventModelSource.ToSlice(projection())!.Name.ShouldNotBe(nameof(BalanceProjection));
    }

    /// <summary>
    /// A bare subscription is not a View slice, and neither is a projection whose document type the
    /// store could not report.
    /// </summary>
    /// <remarks>
    /// A subscription is a side effect with no read model — a green sticky for it would be something
    /// a reader cannot click through to. And a slice named after a projection class rather than its
    /// document would not merge with the declared slice, which is the one thing this source exists to
    /// get right, so no slice is better than a mis-named one.
    /// </remarks>
    [Fact]
    public void a_subscription_or_a_documentless_projection_is_not_a_view_slice()
    {
        ProjectionEventModelSource.ToSlice(projection(SubscriptionType.Subscription)).ShouldBeNull();

        var documentless = new SubscriptionDescriptor(SubscriptionType.EventProjection)
        {
            Name = "Ledger", ImplementationType = T<BalanceProjection>(), ShardNames = [],
        };

        ProjectionEventModelSource.ToSlice(documentless).ShouldBeNull();
    }

    /// <summary>
    /// A multi-stream or event projection with a document is a View slice too — the pattern is about
    /// what the slice <em>is</em>, not about how the store shards it.
    /// </summary>
    [Fact]
    public void a_multi_stream_projection_is_also_a_view_slice()
    {
        ProjectionEventModelSource
            .ToSlice(projection(SubscriptionType.MultiStreamProjection, typeof(AccountOpened)))
            .ShouldNotBeNull()
            .Pattern.ShouldBe(SlicePattern.View);
    }

    [Fact]
    public async Task the_source_reads_the_stores_registry()
    {
        var services = new ServiceCollection()
            .AddProjectionEventModelSource(_ => [storeWith(projection(applied: [typeof(AccountOpened)]))])
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.DiscoverAsync(services, TestContext.Current.CancellationToken)).Single();

        model.Slices.ShouldHaveSingleItem().Name.ShouldBe(nameof(AccountBalance));
    }

    /// <summary>
    /// Every slice this source contributes is stamped <see cref="EventModelProvenance.Derived" /> —
    /// which is what lets it win a role a declaration disagrees about, and what makes the
    /// disagreement a recorded hotspot rather than a silent drop.
    /// </summary>
    [Fact]
    public async Task the_sources_slices_are_stamped_derived()
    {
        var services = new ServiceCollection()
            .AddProjectionEventModelSource(_ => [storeWith(projection(applied: [typeof(AccountOpened)]))])
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.DiscoverAsync(services, TestContext.Current.CancellationToken)).Single();

        model.Slices.Single().Provenance.ShouldBe(EventModelProvenance.Derived);
    }

    /// <summary>
    /// <b>The acceptance criterion.</b> A spec-declared slice and the store-derived one of the same
    /// name merge into <em>one</em> slice carrying both claims.
    /// </summary>
    [Fact]
    public async Task a_declared_slice_and_the_derived_one_merge_into_one()
    {
        var declared = new EventModelDescriptor(ProjectionEventModelSource.DefaultModelName,
        [
            EventModelSliceDescriptor.Named(nameof(AccountBalance)) with
            {
                Domain = "Banking",
                Chapter = "Onboarding",
            },
        ]);

        var services = new ServiceCollection()
            .AddEventModelSource(new StubSource(declared))
            .AddProjectionEventModelSource(_ => [storeWith(projection(applied: [typeof(AccountOpened)]))])
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.AssembleAsync(services, TestContext.Current.CancellationToken)).Single();

        var slice = model.Slices.ShouldHaveSingleItem();

        // The declaration keeps the roles only it claims...
        slice.Domain.ShouldBe("Banking");
        slice.Chapter.ShouldBe("Onboarding");
        slice.ProvenanceFor(EventModelRole.Domain).ShouldBe(EventModelProvenance.Declared);

        // ...and the store owns the ones it derived.
        slice.ConsumedEvents.ShouldHaveSingleItem().ShouldBe(T<AccountOpened>());
        slice.ProvenanceFor(EventModelRole.ConsumedEvents).ShouldBe(EventModelProvenance.Derived);
        slice.ProvenanceFor(EventModelRole.ReadModelTypes).ShouldBe(EventModelProvenance.Derived);

        // Nothing was lost, so nothing is a hotspot.
        slice.Hotspots.ShouldBeEmpty();
    }

    /// <summary>
    /// A store that cannot describe itself contributes nothing, rather than an empty model.
    /// </summary>
    /// <remarks>
    /// An empty slice list would read as "this store has no projections", which is a different and
    /// wrong claim — and one a <see cref="EventModelProvenance.Derived" /> rung would then win with.
    /// </remarks>
    [Fact]
    public async Task a_store_that_cannot_describe_itself_contributes_nothing()
    {
        var store = Substitute.For<IEventStore>();
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(null));

        var services = new ServiceCollection()
            .AddProjectionEventModelSource(_ => [store])
            .BuildServiceProvider();

        (await EventModelDiscovery.DiscoverAsync(services, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    /// <summary>
    /// Two stores projecting the same document type contribute one slice, not a pair that merges with
    /// itself and records a disagreement between two views of one registration.
    /// </summary>
    [Fact]
    public async Task two_stores_projecting_the_same_document_contribute_one_slice()
    {
        var services = new ServiceCollection()
            .AddProjectionEventModelSource(_ =>
            [
                storeWith(projection(applied: [typeof(AccountOpened)])),
                storeWith(projection(applied: [typeof(MoneyDeposited)])),
            ])
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.DiscoverAsync(services, TestContext.Current.CancellationToken)).Single();

        model.Slices.ShouldHaveSingleItem().ConsumedEvents.ShouldHaveSingleItem().ShouldBe(T<AccountOpened>());
    }

    /// <summary>
    /// The store-derived slice links to the slice that emits the event it applies — the State View
    /// arrow, arriving with no human having drawn it.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the three issues landing together: #825 supplies the
    /// <c>ConsumedEvents</c>, #824 gives them a role and an element, and #823 turns the pair into an
    /// edge across slices.
    /// </remarks>
    [Fact]
    public void a_derived_view_slice_links_to_the_command_slice_that_emits_its_event()
    {
        var open = EventModelSliceDescriptor.Named("OpenAccount") with { EmittedEvents = [T<AccountOpened>()] };
        var derived = ProjectionEventModelSource.ToSlice(projection(applied: [typeof(AccountOpened)]))!;

        var link = new EventModelDescriptor("Bank", [open, derived]).Links.ShouldHaveSingleItem();

        link.Kind.ShouldBe(EventModelLinkKind.EventConsumed);
        link.FromSlice.ShouldBe("OpenAccount");
        link.ToSlice.ShouldBe(nameof(AccountBalance));
    }

    /// <summary>
    /// <see cref="SubscriptionDescriptor.AppliedEvents" /> is filled from the projection's real apply
    /// set — the store-side half that makes all of the above derivable.
    /// </summary>
    [Fact]
    public void applied_events_come_from_an_aggregate_projections_apply_set()
    {
        var descriptor = new SubscriptionDescriptor(
            new FakeAggregateProjection([typeof(AccountOpened), typeof(MoneyDeposited)]),
            Substitute.For<IEventStore>());

        descriptor.AppliedEvents.ShouldBe([T<AccountOpened>(), T<MoneyDeposited>()]);
        descriptor.AggregateType.ShouldBe(T<AccountBalance>());
    }

    /// <summary>
    /// Anything that is not an aggregate projection falls back to the event allow list, which is what
    /// an <c>EventProjection</c> or a filtered subscription carries.
    /// </summary>
    /// <remarks>
    /// An empty list there is a legitimate answer meaning "did not narrow its event types" — an
    /// unfiltered subscription sees every event in the store — so it is not treated as "applies
    /// nothing".
    /// </remarks>
    [Fact]
    public void applied_events_fall_back_to_the_event_filter_allow_list()
    {
        var filtered = new FakeFilterableSubscription();
        filtered.IncludeType<AccountOpened>();

        new SubscriptionDescriptor(filtered, Substitute.For<IEventStore>())
            .AppliedEvents.ShouldBe([T<AccountOpened>()]);

        new SubscriptionDescriptor(new FakeFilterableSubscription(), Substitute.For<IEventStore>())
            .AppliedEvents.ShouldBeEmpty();
    }

    private sealed class StubSource(EventModelDescriptor descriptor) : IEventModelDefinitionSource
    {
        public Uri Subject => new("event-model://declared");

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
            => Task.FromResult<EventModelDescriptor?>(descriptor);
    }

    /// <summary>
    /// The narrowest thing that is both an <see cref="ISubscriptionSource" /> and an
    /// <see cref="IAggregateProjection" /> — the shape every self-aggregating projection presents to
    /// <see cref="SubscriptionDescriptor" />.
    /// </summary>
    private sealed class FakeAggregateProjection(Type[] events) : ISubscriptionSource, IAggregateProjection
    {
        public string Name => nameof(AccountBalance);
        public uint Version => 1;
        public SubscriptionType Type => SubscriptionType.SingleStreamProjection;
        public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;
        public ShardName[] ShardNames() => [];
        public Type ImplementationType => typeof(BalanceProjection);
        public SubscriptionDescriptor Describe(IEventStore store) => new(this, store);

        public Type IdentityType => typeof(Guid);
        public Type AggregateType => typeof(AccountBalance);
        public AggregationScope Scope => AggregationScope.SingleStream;
        public Type[] AllEventTypes => events;
        public AsyncOptions Options { get; } = new();
        public NaturalKeyDefinition? NaturalKeyDefinition => null;
    }

    private sealed class FakeFilterableSubscription : EventFilterable, ISubscriptionSource
    {
        public string Name => "Ledger";
        public uint Version => 1;
        public SubscriptionType Type => SubscriptionType.Subscription;
        public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;
        public ShardName[] ShardNames() => [];
        public Type ImplementationType => typeof(FakeFilterableSubscription);
        public SubscriptionDescriptor Describe(IEventStore store) => new(this, store);
    }

    public class AccountOpened { }
    public class MoneyDeposited { }
    public class AccountBalance { }
    public class BalanceProjection { }
}
