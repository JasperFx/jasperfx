using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#703: the descriptor carries provenance, and production wins over declarations.
/// </summary>
public class EventModelProvenanceTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static EventModelSliceDescriptor Slice(EventModelProvenance? provenance = null) =>
        EventModelSliceDescriptor.Named("PlaceOrder") with { Provenance = provenance };

    #region the ladder

    [Fact]
    public void a_declared_role_is_overridden_by_a_derived_one()
    {
        var declared = Slice(EventModelProvenance.Declared) with { EmittedEvents = [T<OrderPlaced>()] };
        var derived = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderShipped>()] };

        declared.Merge(derived).EmittedEvents.ShouldHaveSingleItem().FullName.ShouldBe(T<OrderShipped>().FullName);
    }

    [Fact]
    public void a_derived_role_is_overridden_by_an_observed_one()
    {
        var derived = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] };
        var observed = Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<OrderShipped>()] };

        derived.Merge(observed).EmittedEvents.ShouldHaveSingleItem().FullName.ShouldBe(T<OrderShipped>().FullName);
    }

    [Fact]
    public void the_ladder_decides_regardless_of_the_order_the_sources_are_merged_in()
    {
        var declared = Slice(EventModelProvenance.Declared) with { CommandType = T<PlaceOrder>() };
        var observed = Slice(EventModelProvenance.Observed) with { CommandType = T<ShipOrder>() };

        // This is the inversion: before jasperfx#703, whichever side was merged first won outright,
        // which is why WolverineEventModelSource had to be registered at index 0.
        declared.Merge(observed).CommandType!.FullName.ShouldBe(T<ShipOrder>().FullName);
        observed.Merge(declared).CommandType!.FullName.ShouldBe(T<ShipOrder>().FullName);
    }

    #endregion

    #region per claimed role, not wholesale

    [Fact]
    public void a_source_that_does_not_claim_a_role_never_overrides_one_that_does()
    {
        var declared = Slice(EventModelProvenance.Declared) with
        {
            Domain = "Orders",
            Specifications = [new SpecificationDescriptor("Place Order/Place an order")],
            TriggerLabel = "User clicks Place Order",
        };

        // Production sees events happen. It has no opinion whatsoever about what the slice is called,
        // which bounded context it belongs to, or which spec covers it.
        var observed = Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<OrderPlaced>()] };

        var merged = declared.Merge(observed);

        merged.Domain.ShouldBe("Orders");
        merged.Specifications.ShouldHaveSingleItem().Identity.ShouldBe("Place Order/Place an order");
        merged.TriggerLabel.ShouldBe("User clicks Place Order");
        merged.EmittedEvents.ShouldHaveSingleItem().FullName.ShouldBe(T<OrderPlaced>().FullName);
    }

    [Fact]
    public void a_name_declared_by_an_overlay_survives_every_rung()
    {
        // The acceptance criterion of jasperfx#703, end to end: three sources, a declared role beaten
        // by a derived one, a derived role beaten by an observed one, and the declaration-only roles
        // untouched by either.
        var overlay = new EventModelDescriptor("Orders", [
            EventModelSliceDescriptor.Named("PlaceOrder") with
            {
                Provenance = EventModelProvenance.Declared,
                Domain = "Ordering",
                TriggerLabel = "User clicks Place Order",
                AggregateTypes = [T<Cart>()],
            }
        ]);

        var derived = new EventModelDescriptor("Orders", [
            EventModelSliceDescriptor.Named("PlaceOrder") with
            {
                Provenance = EventModelProvenance.Derived,
                CommandType = T<PlaceOrder>(),
                AggregateTypes = [T<Order>()],
                EmittedEvents = [T<OrderPlaced>()],
            }
        ]);

        var observed = new EventModelDescriptor("Orders", [
            EventModelSliceDescriptor.Named("PlaceOrder") with
            {
                Provenance = EventModelProvenance.Observed,
                EmittedEvents = [T<OrderPlaced>(), T<AuditRecorded>()],
            }
        ]);

        var merged = EventModelDescriptor.Merge("Orders", [overlay, derived, observed]);
        var slice = merged.Slices.ShouldHaveSingleItem();

        // Production wins the events.
        slice.EmittedEvents.Select(x => x.FullName)
            .ShouldBe([T<OrderPlaced>().FullName, T<AuditRecorded>().FullName]);

        // The code wins the aggregate over the overlay's guess.
        slice.AggregateTypes.ShouldHaveSingleItem().FullName.ShouldBe(T<Order>().FullName);

        // The declaration keeps everything nothing else claims.
        slice.Domain.ShouldBe("Ordering");
        slice.TriggerLabel.ShouldBe("User clicks Place Order");
    }

    #endregion

    #region per-role attribution

    [Fact]
    public void a_merged_slice_reports_which_rung_claimed_each_role()
    {
        var overlay = EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            Provenance = EventModelProvenance.Declared, Domain = "Ordering",
        };

        var derived = EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            Provenance = EventModelProvenance.Derived, CommandType = T<PlaceOrder>(),
        };

        var observed = EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            Provenance = EventModelProvenance.Observed, EmittedEvents = [T<OrderPlaced>()],
        };

        var merged = overlay.Merge(derived).Merge(observed);

        merged.ProvenanceFor(EventModelRole.Domain).ShouldBe(EventModelProvenance.Declared);
        merged.ProvenanceFor(EventModelRole.CommandType).ShouldBe(EventModelProvenance.Derived);
        merged.ProvenanceFor(EventModelRole.EmittedEvents).ShouldBe(EventModelProvenance.Observed);

        // Nothing claimed the handler, so nobody is attributed with it.
        merged.ProvenanceFor(EventModelRole.HandlerType).ShouldBeNull();
    }

    [Fact]
    public void an_unmerged_slice_attributes_every_role_it_claims_to_its_own_rung()
    {
        var slice = Slice(EventModelProvenance.Derived) with
        {
            CommandType = T<PlaceOrder>(), EmittedEvents = [T<OrderPlaced>()],
        };

        slice.ProvenanceFor(EventModelRole.CommandType).ShouldBe(EventModelProvenance.Derived);
        slice.ProvenanceFor(EventModelRole.EmittedEvents).ShouldBe(EventModelProvenance.Derived);
        slice.ProvenanceFor(EventModelRole.Domain).ShouldBeNull();
    }

    [Fact]
    public void an_unattributed_slice_reads_as_declared()
    {
        var slice = Slice() with { CommandType = T<PlaceOrder>() };

        slice.Provenance.ShouldBeNull();
        slice.ProvenanceFor(EventModelRole.CommandType).ShouldBe(EventModelProvenance.Declared);
    }

    [Fact]
    public void a_merged_slice_summarises_as_the_highest_rung_that_contributed()
    {
        var merged = Slice(EventModelProvenance.Declared).Merge(Slice(EventModelProvenance.Observed));

        merged.Provenance.ShouldBe(EventModelProvenance.Observed);
    }

    #endregion

    #region list semantics

    [Fact]
    public void a_higher_rung_replaces_a_list_rather_than_unioning_with_it()
    {
        var derived = Slice(EventModelProvenance.Derived) with
        {
            EmittedEvents = [T<OrderPlaced>(), T<OrderShipped>()],
        };

        var observed = Slice(EventModelProvenance.Observed) with
        {
            EmittedEvents = [T<OrderPlaced>(), T<AuditRecorded>()],
        };

        // Unioning would invent a slice emitting three events that nobody ever claimed. Production
        // wins outright; what became of OrderShipped is a finding, and jasperfx#704 records it.
        derived.Merge(observed).EmittedEvents.Select(x => x.FullName)
            .ShouldBe([T<OrderPlaced>().FullName, T<AuditRecorded>().FullName]);
    }

    [Fact]
    public void two_sources_on_the_same_rung_still_union_their_lists()
    {
        var first = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] };
        var second = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderShipped>()] };

        first.Merge(second).EmittedEvents.Select(x => x.FullName)
            .ShouldBe([T<OrderPlaced>().FullName, T<OrderShipped>().FullName]);
    }

    #endregion

    #region compatibility

    [Fact]
    public void unattributed_sources_merge_exactly_as_they_did_before()
    {
        // Neither side is stamped, so both sit on Declared, every role ties, and every tie resolves
        // on the order the sources were given -- which is what Merge did for every role before
        // jasperfx#703. This is the hinge the whole change rests on.
        var first = Slice() with
        {
            CommandType = T<PlaceOrder>(),
            EmittedEvents = [T<OrderPlaced>()],
            Domain = "Ordering",
        };

        var second = Slice() with
        {
            CommandType = T<ShipOrder>(),
            EmittedEvents = [T<OrderShipped>()],
            Domain = "Fulfilment",
            HandlerType = T<OrderHandler>(),
        };

        var merged = first.Merge(second);

        merged.CommandType!.FullName.ShouldBe(T<PlaceOrder>().FullName);
        merged.Domain.ShouldBe("Ordering");
        merged.EmittedEvents.Select(x => x.FullName)
            .ShouldBe([T<OrderPlaced>().FullName, T<OrderShipped>().FullName]);
        merged.HandlerType!.FullName.ShouldBe(T<OrderHandler>().FullName);
        merged.Provenance.ShouldBeNull();

        // The merged *values* are what they always were. jasperfx#704 additionally records the two
        // claims first-wins dropped here -- the competing command types and domains -- as hotspots,
        // which is the one thing about an unstamped merge that does change.
        merged.Hotspots.Count(x => x.Origin == HotspotOrigin.SourceDisagreement).ShouldBe(2);
    }

    #endregion

    #region rendering

    [Fact]
    public void elements_carry_the_provenance_of_the_role_they_render()
    {
        var slice = EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            Provenance = EventModelProvenance.Declared, TriggerLabel = "User clicks Place Order",
        };

        var merged = slice
            .Merge(EventModelSliceDescriptor.Named("PlaceOrder") with
            {
                Provenance = EventModelProvenance.Derived, CommandType = T<PlaceOrder>(),
            })
            .Merge(EventModelSliceDescriptor.Named("PlaceOrder") with
            {
                Provenance = EventModelProvenance.Observed, EmittedEvents = [T<OrderPlaced>()],
            });

        EventModelProvenance? provenanceOf(EventModelElementKind kind)
            => merged.Elements.Single(x => x.Kind == kind).Provenance;

        provenanceOf(EventModelElementKind.Trigger).ShouldBe(EventModelProvenance.Declared);
        provenanceOf(EventModelElementKind.Command).ShouldBe(EventModelProvenance.Derived);
        provenanceOf(EventModelElementKind.Event).ShouldBe(EventModelProvenance.Observed);
    }

    [Fact]
    public void elements_of_an_unattributed_slice_read_as_the_declared_floor()
    {
        var slice = Slice() with { CommandType = T<PlaceOrder>() };

        // One rule, not two: an unattributed source is treated as a declaration by the merge, so its
        // elements say so too rather than reporting a null the merge does not honour.
        slice.Elements.Single(x => x.Kind == EventModelElementKind.Command).Provenance
            .ShouldBe(EventModelProvenance.Declared);
    }

    #endregion

    #region discovery

    [Fact]
    public async Task discovery_stamps_each_source_with_its_own_rung()
    {
        var services = new ServiceCollection()
            .AddEventModelSource(new StubSource("declared", EventModelProvenance.Declared,
                s => s with { Domain = "Ordering" }))
            .AddEventModelSource(new StubSource("observed", EventModelProvenance.Observed,
                s => s with { EmittedEvents = [T<OrderPlaced>()] }))
            .BuildServiceProvider();

        var descriptors = await EventModelDiscovery.DiscoverAsync(services);

        descriptors[0].Slices[0].Provenance.ShouldBe(EventModelProvenance.Declared);
        descriptors[1].Slices[0].Provenance.ShouldBe(EventModelProvenance.Observed);
    }

    [Fact]
    public async Task a_source_that_stamps_its_own_slices_is_left_alone()
    {
        // WithProvenance only fills in unattributed slices, so a source that can attribute per slice
        // -- a generator emitting some slices from Gherkin and some from code -- is not flattened.
        var services = new ServiceCollection()
            .AddEventModelSource(new StubSource("mixed", EventModelProvenance.Declared,
                s => s with { Provenance = EventModelProvenance.Observed }))
            .BuildServiceProvider();

        var descriptors = await EventModelDiscovery.DiscoverAsync(services);

        descriptors[0].Slices[0].Provenance.ShouldBe(EventModelProvenance.Observed);
    }

    [Fact]
    public async Task assembly_lets_a_later_registered_observed_source_beat_an_earlier_derived_one()
    {
        // Registration order used to be the whole mechanism. Here the derived source is registered
        // first and still loses the events, because the ladder decides now.
        var services = new ServiceCollection()
            .AddEventModelSource(new StubSource("derived", EventModelProvenance.Derived,
                s => s with { EmittedEvents = [T<OrderPlaced>()], CommandType = T<PlaceOrder>() }))
            .AddEventModelSource(new StubSource("observed", EventModelProvenance.Observed,
                s => s with { EmittedEvents = [T<AuditRecorded>()] }))
            .BuildServiceProvider();

        var models = await EventModelDiscovery.AssembleAsync(services);
        var slice = models.ShouldHaveSingleItem().Slices.ShouldHaveSingleItem();

        slice.EmittedEvents.ShouldHaveSingleItem().FullName.ShouldBe(T<AuditRecorded>().FullName);

        // ...and the derived command survives, because production never claimed one.
        slice.CommandType!.FullName.ShouldBe(T<PlaceOrder>().FullName);
    }

    [Fact]
    public void the_default_rung_for_a_source_is_declared()
    {
        // A default interface member, so every existing IEventModelDefinitionSource keeps compiling
        // -- UnstampedSource declares nothing -- and lands on the rung an overlay or a spec belongs on.
        IEventModelDefinitionSource unstamped = new UnstampedSource();

        unstamped.Provenance.ShouldBe(EventModelProvenance.Declared);
    }

    #endregion

    private sealed class StubSource : IEventModelDefinitionSource
    {
        private readonly Func<EventModelSliceDescriptor, EventModelSliceDescriptor> _configure;

        public StubSource(string name, EventModelProvenance provenance,
            Func<EventModelSliceDescriptor, EventModelSliceDescriptor> configure)
        {
            Subject = new Uri($"event-model://{name}");
            Provenance = provenance;
            _configure = configure;
        }

        public Uri Subject { get; }

        public EventModelProvenance Provenance { get; }

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
            => Task.FromResult<EventModelDescriptor?>(
                new EventModelDescriptor("Orders", [_configure(EventModelSliceDescriptor.Named("PlaceOrder"))]));
    }

    private sealed class UnstampedSource : IEventModelDefinitionSource
    {
        public Uri Subject => new("event-model://unstamped");

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
            => Task.FromResult<EventModelDescriptor?>(null);
    }

    public class PlaceOrder { }
    public class ShipOrder { }
    public class OrderHandler { }
    public class Order { }
    public class Cart { }
    public class OrderPlaced { }
    public class OrderShipped { }
    public class AuditRecorded { }
}
