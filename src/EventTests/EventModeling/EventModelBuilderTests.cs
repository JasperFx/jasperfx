using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

// jasperfx#687 (reshaped 2026-08-20): the fluent API is an OVERLAY. It names, groups,
// annotates and links; it never declares a role. Roles come from derived sources and the
// one documented escape hatch.
public class EventModelBuilderTests
{
    [Fact]
    public void overlay_names_groups_annotates_and_links()
    {
        var builder = new EventModelBuilder { Name = "Orders" };
        new SampleOverlay().Configure(builder);

        var slices = builder.BuildSlices();
        slices.Count.ShouldBe(2);

        var place = slices[0];
        place.Name.ShouldBe("PlaceOrder");
        place.Domain.ShouldBe("Orders");
        place.TriggerLabel.ShouldBe("User clicks Place Order");
        place.Specifications.Select(x => x.Identity).ShouldBe(new[] { "Place Order/Place an order" });

        // and nothing role-shaped was declared
        place.Pattern.ShouldBeNull();
        place.TriggerKind.ShouldBeNull();
        place.CommandType.ShouldBeNull();
        place.HandlerType.ShouldBeNull();
        place.EmittedEvents.ShouldBeEmpty();
        place.AggregateTypes.ShouldBeEmpty();
        place.ProjectionTypes.ShouldBeEmpty();
        place.ReadModelTypes.ShouldBeEmpty();

        var ship = slices[1];
        ship.Domain.ShouldBe("Fulfilment"); // slice-level InDomain overrides the builder default
    }

    [Fact]
    public void builder_level_domain_applies_to_slices_opened_after_it()
    {
        var builder = new EventModelBuilder();
        builder.Slice("before");
        builder.InDomain("Billing");
        builder.Slice("after");

        var slices = builder.BuildSlices();
        slices[0].Domain.ShouldBeNull();
        slices[1].Domain.ShouldBe("Billing");
    }

    [Fact]
    public void multiple_slices_are_kept_in_declaration_order()
    {
        var builder = new EventModelBuilder();
        builder.Slice("first");
        builder.Slice("second");

        builder.BuildSlices().Select(s => s.Name).ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public void build_uses_the_fallback_name_when_unset()
    {
        var builder = new EventModelBuilder();
        builder.Slice("x");

        builder.Build("Fallback").Name.ShouldBe("Fallback");
        new EventModelBuilder { Name = "Explicit" }.Build("Fallback").Name.ShouldBe("Explicit");
    }

    [Fact]
    public void escape_hatch_declares_roles_for_a_flow_not_owned_here()
    {
        var builder = new EventModelBuilder();
        builder.Slice("PartnerRefund")
            .TriggeredBy("Partner posts a refund")
            .ForFlowNotOwnedHere(roles => roles
                .Pattern(SlicePattern.Translation)
                .TriggeredBy<PartnerRefundRequest>(TriggerKind.External)
                .Command<RefundOrder>()
                .HandledBy<RefundHandler>()
                .UsesAggregate<Order>()
                .Emits<OrderRefunded>()
                .Publishes<NotifyAccounting>()
                .Projects<OrderProjection>()
                .Reads<OrderSummary>()
                .ExternalSystem("Partner", ExternalSystemDirection.Inbound, "rabbitmq://queue/partner"));

        var slice = builder.BuildSlices().Single();
        slice.TriggerLabel.ShouldBe("Partner posts a refund");
        slice.Pattern.ShouldBe(SlicePattern.Translation);
        slice.TriggerKind.ShouldBe(TriggerKind.External);
        slice.TriggerType.ShouldBe(TypeDescriptor.For(typeof(PartnerRefundRequest)));
        slice.CommandType.ShouldBe(TypeDescriptor.For(typeof(RefundOrder)));
        slice.HandlerType.ShouldBe(TypeDescriptor.For(typeof(RefundHandler)));
        slice.AggregateTypes.ShouldBe(new[] { TypeDescriptor.For(typeof(Order)) });
        slice.EmittedEvents.ShouldBe(new[] { TypeDescriptor.For(typeof(OrderRefunded)) });
        slice.PublishedMessages.ShouldBe(new[] { TypeDescriptor.For(typeof(NotifyAccounting)) });
        slice.ProjectionTypes.ShouldBe(new[] { TypeDescriptor.For(typeof(OrderProjection)) });
        slice.ReadModelTypes.ShouldBe(new[] { TypeDescriptor.For(typeof(OrderSummary)) });
        slice.ExternalSystems.Single().ShouldBe(new ExternalSystemDescriptor("Partner", ExternalSystemDirection.Inbound, "rabbitmq://queue/partner"));
    }

    public class SampleOverlay : EventModelDefinition
    {
        public override void Configure(EventModelBuilder builder)
        {
            builder.InDomain("Orders");

            builder.Slice("PlaceOrder")
                .TriggeredBy("User clicks Place Order")
                .LinksToSpecification("Place Order/Place an order");

            builder.Slice("ShipOrder")
                .InDomain("Fulfilment");
        }
    }

    public class PartnerRefundRequest { }
    public class RefundOrder { }
    public class RefundHandler { }
    public class Order { }
    public class OrderRefunded { }
    public class NotifyAccounting { }
    public class OrderProjection { }
    public class OrderSummary { }
}
