using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling
{
    /// <summary>
    /// jasperfx#954: one source describing two different slices under one name must not be folded into
    /// a slice that attributes one handler's output to the other.
    /// </summary>
    public class SliceCollisionTests
    {
        private static readonly Uri Wolverine = new("event-model://wolverine/");
        private static readonly Uri WolverineHttp = new("event-model://wolverine-http/");

        private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

        // The shape Wolverine derives under MultipleHandlerBehavior.Separated (JasperFx/wolverine#4829):
        // one message, one handler per module, both slices named for the message, same source.
        private static EventModelSliceDescriptor shipping(Uri? origin = null) =>
            EventModelSliceDescriptor.Named("OrderPlaced") with
            {
                CommandType = T<CollisionModules.OrderPlaced>(),
                HandlerType = T<CollisionModules.Shipping.OrderPlacedHandler>(),
                PublishedMessages = [T<CollisionModules.Shipping.ShipmentRequested>()],
                Origin = origin ?? Wolverine,
                Provenance = EventModelProvenance.Derived,
            };

        private static EventModelSliceDescriptor billing(Uri? origin = null) =>
            EventModelSliceDescriptor.Named("OrderPlaced") with
            {
                CommandType = T<CollisionModules.OrderPlaced>(),
                HandlerType = T<CollisionModules.Billing.OrderPlacedHandler>(),
                PublishedMessages = [T<CollisionModules.Billing.InvoiceRequested>()],
                Origin = origin ?? Wolverine,
                Provenance = EventModelProvenance.Derived,
            };

        [Fact]
        public void two_handlers_from_one_source_do_not_fold_into_one_slice()
        {
            var merged = shipping().Merge(billing());

            // The first slice survives intact -- in particular it does NOT claim Billing's message.
            merged.HandlerType!.FullName.ShouldBe(typeof(CollisionModules.Shipping.OrderPlacedHandler).FullName);
            merged.PublishedMessages.Select(x => x.Name).ShouldBe(["ShipmentRequested"]);

            var hotspot = merged.Hotspots.ShouldHaveSingleItem();
            hotspot.Origin.ShouldBe(HotspotOrigin.SliceCollision);
            hotspot.Role.ShouldBe(EventModelRole.HandlerType);
            hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived,
                typeof(CollisionModules.Shipping.OrderPlacedHandler).FullName!, Wolverine.OriginalString));
            hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived,
                typeof(CollisionModules.Billing.OrderPlacedHandler).FullName!, Wolverine.OriginalString));
            hotspot.Text.ShouldBe(
                "event-model://wolverine/ describes two slices named 'OrderPlaced' with different handlers — " +
                "kept EventTests.EventModeling.CollisionModules.Shipping.OrderPlacedHandler, not folded: " +
                "EventTests.EventModeling.CollisionModules.Billing.OrderPlacedHandler. " +
                "Give each its own name, e.g. by declaring its Domain.");
        }

        [Fact]
        public void the_probe_shape_through_the_model_merge()
        {
            // Exactly what wolverine#4829 measured: one descriptor, two same-named slices.
            var derived = new EventModelDescriptor("app", [shipping(), billing()]);

            var merged = EventModelDescriptor.Merge("app", [derived]);

            var slice = merged.Slices.ShouldHaveSingleItem();
            slice.PublishedMessages.Select(x => x.Name).ShouldBe(["ShipmentRequested"]);
            slice.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.SliceCollision);
        }

        [Fact]
        public void the_dropped_slice_keeps_its_annotations()
        {
            var withPendingSpec = billing() with
            {
                Hotspots = [HotspotDescriptor.PendingSpecification("Billing/Invoices an order")],
            };

            var merged = shipping().Merge(withPendingSpec);

            merged.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.PendingSpecification);
            merged.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.SliceCollision);
        }

        [Fact]
        public void unattributed_slices_are_not_proven_to_be_one_source()
        {
            // No Origin on either side: they may be two sources, so the per-role merge and its
            // SourceDisagreement stand (SourceDisagreementHotspotTests covers that behaviour).
            var merged = (shipping() with { Origin = null }).Merge(billing() with { Origin = null });

            merged.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SliceCollision);
            merged.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.SourceDisagreement
                                               && x.Role == EventModelRole.HandlerType);
        }

        [Fact]
        public void the_same_handler_from_one_source_still_merges()
        {
            var again = shipping() with
            {
                PublishedMessages = [T<CollisionModules.Shipping.ShipmentScheduled>()],
            };

            var merged = shipping().Merge(again);

            merged.PublishedMessages.Select(x => x.Name).ShouldBe(["ShipmentRequested", "ShipmentScheduled"]);
            merged.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void different_sources_still_fold_by_design()
        {
            // Wolverine.HTTP names an endpoint's slice for its request type so the endpoint and the
            // message handler for that command become ONE slice. Different handlers, different sources:
            // not a collision.
            var endpoint = billing(WolverineHttp) with
            {
                HandlerType = T<CollisionModules.Billing.OrderPlacedEndpoint>(),
                PublishedMessages = [],
            };

            var merged = billing().Merge(endpoint);

            merged.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SliceCollision);
        }

        [Fact]
        public void a_handler_scalar_disagrees_by_identity_not_by_simple_name()
        {
            // Across rungs, two handlers that share only a simple name used to compare equal and the
            // loser vanished without a hotspot.
            var declared = shipping() with { Provenance = EventModelProvenance.Declared, Origin = null };

            var merged = declared.Merge(billing());

            merged.HandlerType!.FullName.ShouldBe(typeof(CollisionModules.Billing.OrderPlacedHandler).FullName);

            var hotspot = merged.Hotspots.Single(x => x.Origin == HotspotOrigin.SourceDisagreement
                                                      && x.Role == EventModelRole.HandlerType);

            // Same simple name on both sides, so the full names are what tell them apart.
            hotspot.WinningClaim!.Value.ShouldBe(typeof(CollisionModules.Billing.OrderPlacedHandler).FullName);
            hotspot.LosingClaim!.Value.ShouldBe(typeof(CollisionModules.Shipping.OrderPlacedHandler).FullName);
        }

        [Fact]
        public void a_declared_type_name_agrees_with_the_real_type()
        {
            // jasperfx#798's rule applies to scalars too: a declaration has no assembly, so it matches by name.
            var declared = EventModelSliceDescriptor.Named("OrderPlaced") with
            {
                HandlerType = new TypeDescriptor("OrderPlacedHandler", "OrderPlacedHandler", ""),
                Provenance = EventModelProvenance.Declared,
            };

            var merged = declared.Merge(shipping());

            merged.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void the_new_origin_is_appended_so_the_wire_integers_do_not_move()
        {
            ((int)HotspotOrigin.PendingSpecification).ShouldBe(0);
            ((int)HotspotOrigin.Prose).ShouldBe(1);
            ((int)HotspotOrigin.SourceDisagreement).ShouldBe(2);
            ((int)HotspotOrigin.ModelCollapse).ShouldBe(3);
            ((int)HotspotOrigin.SliceCollision).ShouldBe(4);
        }
    }
}

namespace EventTests.EventModeling.CollisionModules
{
    public record OrderPlaced(Guid OrderId);
}

namespace EventTests.EventModeling.CollisionModules.Shipping
{
    public record ShipmentRequested(Guid OrderId);

    public record ShipmentScheduled(Guid OrderId);

    public class OrderPlacedHandler;
}

namespace EventTests.EventModeling.CollisionModules.Billing
{
    public record InvoiceRequested(Guid OrderId);

    public class OrderPlacedHandler;

    public class OrderPlacedEndpoint;
}
