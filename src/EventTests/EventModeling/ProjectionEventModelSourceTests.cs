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
        => storeAt("store://test", subscriptions);

    private static IEventStore storeAt(string? subjectUri, params SubscriptionDescriptor[] subscriptions)
    {
        var usage = new EventStoreUsage { SubjectUri = subjectUri is null ? null : new Uri(subjectUri) };
        usage.Subscriptions.AddRange(subscriptions);

        var store = Substitute.For<IEventStore>();
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(usage));
        return store;
    }

    private static async Task<EventModelDescriptor> discover(params IEventStore[] stores)
    {
        var services = new ServiceCollection()
            .AddProjectionEventModelSource(_ => stores)
            .BuildServiceProvider();

        return (await EventModelDiscovery.DiscoverAsync(services, TestContext.Current.CancellationToken)).Single();
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
        var model = await discover(
            storeAt("store://ledger", projection(applied: [typeof(AccountOpened)])),
            storeAt("store://audit", projection(applied: [typeof(MoneyDeposited)])));

        model.Slices.ShouldHaveSingleItem().ConsumedEvents.ShouldHaveSingleItem().ShouldBe(T<AccountOpened>());
    }

    /// <summary>
    /// jasperfx#836 — every slice carries <em>which store</em> produced it, not just which rung.
    /// </summary>
    /// <remarks>
    /// The telemetry half of the stack has keyed on the store all along; the modelling half knew only
    /// the rung, so in a modular monolith two modules' identically-named documents were
    /// indistinguishable once they reached a descriptor.
    /// </remarks>
    [Fact]
    public async Task every_slice_carries_the_store_it_came_from()
    {
        var model = await discover(storeAt("store://ledger", projection(applied: [typeof(AccountOpened)])));

        model.Slices.ShouldHaveSingleItem().Origin.ShouldBe(new Uri("store://ledger"));
    }

    /// <summary>
    /// A store whose usage carries no subject of its own falls back to the source's subject — which a
    /// store registering one instance per store mints distinctly.
    /// </summary>
    [Fact]
    public async Task a_store_with_no_subject_of_its_own_falls_back_to_the_sources_subject()
    {
        var services = new ServiceCollection()
            .AddProjectionEventModelSource(
                _ => [storeAt(null, projection(applied: [typeof(AccountOpened)]))],
                subject: new Uri("event-model://projections/ledger"))
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.DiscoverAsync(services, TestContext.Current.CancellationToken)).Single();

        model.Slices.ShouldHaveSingleItem().Origin.ShouldBe(new Uri("event-model://projections/ledger"));
    }

    /// <summary>
    /// <b>jasperfx#836's acceptance criterion.</b> When the dedupe drops a slice from a
    /// <em>different</em> store, the loss is a <see cref="HotspotOrigin.SourceDisagreement" /> hotspot
    /// naming both stores rather than a silent discard.
    /// </summary>
    /// <remarks>
    /// In a modular monolith each module registers its own ancillary store, and two modules owning a
    /// document type of the same simple name — an <c>AuditEntry</c>, a <c>Summary</c>, a
    /// <c>Settings</c> — is ordinary rather than exotic. Every other merge in the system already
    /// records a dropped claim (jasperfx#704); this one was the exception.
    /// </remarks>
    [Fact]
    public async Task the_slice_a_dedupe_drops_leaves_a_disagreement_hotspot()
    {
        var model = await discover(
            storeAt("store://ledger", projection(applied: [typeof(AccountOpened)])),
            storeAt("store://audit", projection(applied: [typeof(MoneyDeposited)])));

        var hotspot = model.Slices.ShouldHaveSingleItem().Hotspots.ShouldHaveSingleItem();

        hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
        hotspot.Role.ShouldBe(EventModelRole.Origin);
        hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "store://ledger"));
        hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "store://audit"));
    }

    /// <summary>
    /// Two views of <em>one</em> store's registration still merge in silence — which is what the
    /// dedupe was protecting against, and is untouched.
    /// </summary>
    [Fact]
    public async Task two_views_of_one_store_do_not_disagree_with_themselves()
    {
        var model = await discover(
            storeAt("store://ledger", projection(applied: [typeof(AccountOpened)])),
            storeAt("store://ledger", projection(applied: [typeof(MoneyDeposited)])));

        model.Slices.ShouldHaveSingleItem().Hotspots.ShouldBeEmpty();
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
    /// jasperfx#829 — the stream lifecycle events every aggregation projection handles are not events
    /// the read model <em>consumes</em>, and do not become stickies.
    /// </summary>
    /// <remarks>
    /// An aggregate declaring exactly two <c>Apply</c> methods reported four event types, because
    /// <c>determineEventTypes()</c> concatenates <see cref="Archived" /> and <see cref="Compacted{T}" />
    /// onto every non-empty apply set. Correct about what the projection handles; wrong as orange
    /// stickies for events the application never wrote and no command slice emits, so they link to
    /// nothing.
    /// </remarks>
    [Fact]
    public void stream_lifecycle_events_are_not_consumed_events()
    {
        var withLifecycle = projection(applied:
            [typeof(AccountOpened), typeof(Archived), typeof(Compacted<AccountBalance>), typeof(MoneyDeposited)]);

        ProjectionEventModelSource.ToSlice(withLifecycle)
            .ShouldNotBeNull()
            .ConsumedEvents.ShouldBe([T<AccountOpened>(), T<MoneyDeposited>()]);
    }

    /// <summary>
    /// The filter is on type identity, not on a name — an application type that happens to be called
    /// <c>Archived</c> keeps its sticky.
    /// </summary>
    /// <remarks>
    /// The pair this test forms with the one above is the whole of the ruling: dropping the two the
    /// framework adds, and dropping nothing the application declared. A name-only filter would pass
    /// the first and fail this one.
    /// </remarks>
    [Fact]
    public void an_application_type_with_a_lifecycle_events_name_is_kept()
    {
        ProjectionEventModelSource.IsStreamLifecycleEvent(T<Archived>()).ShouldBeTrue();
        ProjectionEventModelSource.IsStreamLifecycleEvent(T<Compacted<AccountBalance>>()).ShouldBeTrue();

        // The SAME short name, in the application's own assembly. A name-only filter drops this.
        ProjectionEventModelSource
            .IsStreamLifecycleEvent(new TypeDescriptor("Archived", "MyApp.Archived", "MyApp"))
            .ShouldBeFalse();

        ProjectionEventModelSource
            .IsStreamLifecycleEvent(new TypeDescriptor(typeof(Compacted<>).Name, "MyApp.Compacted`1", "MyApp"))
            .ShouldBeFalse();

        ProjectionEventModelSource.IsStreamLifecycleEvent(T<AccountOpened>()).ShouldBeFalse();
    }

    /// <summary>
    /// The assumption the filter rests on, pinned rather than trusted: a closed
    /// <see cref="Compacted{T}" /> carries the open generic's <see cref="Type.Name" />, so matching on
    /// <c>typeof(Compacted&lt;&gt;).Name</c> catches every closure without enumerating them.
    /// </summary>
    /// <remarks>
    /// If this ever stopped holding, the filter above would silently stop dropping <c>Compacted</c>
    /// for every aggregate at once, and the only symptom would be an extra sticky on a canvas.
    /// </remarks>
    [Fact]
    public void a_closed_compacted_shares_the_open_generics_name()
    {
        T<Compacted<AccountBalance>>().Name.ShouldBe(typeof(Compacted<>).Name);
        T<Compacted<AccountBalance>>().Name.ShouldNotBe(T<Archived>().Name);
    }

    /// <summary>
    /// A projection whose apply set is <em>only</em> lifecycle events contributes an empty
    /// <c>ConsumedEvents</c> — and is still a slice.
    /// </summary>
    /// <remarks>
    /// The slice is what a reader clicks through to, and it still has a projection and a document to
    /// show. Dropping the slice because the filter emptied one role would hide a registered
    /// projection from the canvas entirely, which is the failure jasperfx#825 exists to remove.
    /// </remarks>
    [Fact]
    public void a_projection_with_only_lifecycle_events_is_still_a_slice()
    {
        var slice = ProjectionEventModelSource
            .ToSlice(projection(applied: [typeof(Archived), typeof(Compacted<AccountBalance>)]))
            .ShouldNotBeNull();

        slice.ConsumedEvents.ShouldBeEmpty();
        slice.ReadModelTypes.ShouldHaveSingleItem().ShouldBe(T<AccountBalance>());
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
