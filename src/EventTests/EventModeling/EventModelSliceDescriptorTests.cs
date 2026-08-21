using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

public class EventModelSliceDescriptorTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    [Fact]
    public void original_positional_shape_still_compiles_and_the_additive_slots_default_safely()
    {
        // The 2.x positional constructor is what CritterWatch and the source generator call.
        // Every jasperfx#687 addition must default to "not derived" so precompiled callers and
        // older JSON payloads keep working.
        var slice = new EventModelSliceDescriptor("place", null, null, T<PlaceOrder>(), T<OrderHandler>(),
            new[] { T<OrderPlaced>() }, Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>());

        slice.Pattern.ShouldBeNull();
        slice.TriggerKind.ShouldBeNull();
        slice.TriggerOrigin.ShouldBeNull();
        slice.AggregateTypes.ShouldBeEmpty();
        slice.PublishedMessages.ShouldBeEmpty();
        slice.ExternalSystems.ShouldBeEmpty();
        slice.Hotspots.ShouldBeEmpty();
        slice.Specifications.ShouldBeEmpty();
        slice.Domain.ShouldBeNull();
    }

    [Fact]
    public void named_is_an_empty_slice()
    {
        var slice = EventModelSliceDescriptor.Named("x");
        slice.Name.ShouldBe("x");
        slice.Elements.ShouldBeEmpty();
        slice.Edges.ShouldBeEmpty();
    }

    [Fact]
    public void command_slice_renders_every_element_with_id_kind_and_lane_and_directed_edges()
    {
        var slice = new EventModelSliceDescriptor("PlaceOrder", "User clicks Place Order", null,
                T<PlaceOrder>(), T<OrderHandler>(),
                new[] { T<OrderPlaced>() }, new[] { T<OrderProjection>() }, new[] { T<OrderSummary>() })
        {
            Pattern = SlicePattern.Command,
            TriggerKind = TriggerKind.Human,
            AggregateTypes = new[] { T<Order>() },
            PublishedMessages = new[] { T<NotifyWarehouse>() },
            ExternalSystems = new[] { new ExternalSystemDescriptor("Warehouse", ExternalSystemDirection.Outbound) },
            Hotspots = new[] { HotspotDescriptor.PendingSpecification("Place Order/Rejects empty cart") },
        };

        var elements = slice.Elements;

        // one element per role, each with the canonical lane
        elements.Select(e => e.Kind).ShouldBe(new[]
        {
            EventModelElementKind.Trigger,
            EventModelElementKind.Hotspot,
            EventModelElementKind.Command,
            EventModelElementKind.Handler,
            EventModelElementKind.Aggregate,
            EventModelElementKind.Event,
            EventModelElementKind.Message,
            EventModelElementKind.Projection,
            EventModelElementKind.ReadModel,
            EventModelElementKind.ExternalSystem,
        });

        elements.Single(e => e.Kind == EventModelElementKind.Trigger).Lane.ShouldBe(EventModelLane.Wireframe);
        elements.Single(e => e.Kind == EventModelElementKind.Hotspot).Lane.ShouldBe(EventModelLane.Wireframe);
        elements.Single(e => e.Kind == EventModelElementKind.Command).Lane.ShouldBe(EventModelLane.Command);
        elements.Single(e => e.Kind == EventModelElementKind.Handler).Lane.ShouldBe(EventModelLane.Command);
        elements.Single(e => e.Kind == EventModelElementKind.Aggregate).Lane.ShouldBe(EventModelLane.Command);
        elements.Single(e => e.Kind == EventModelElementKind.Event).Lane.ShouldBe(EventModelLane.EventStream);
        elements.Single(e => e.Kind == EventModelElementKind.Message).Lane.ShouldBe(EventModelLane.EventStream);
        elements.Single(e => e.Kind == EventModelElementKind.Projection).Lane.ShouldBe(EventModelLane.ReadModel);
        elements.Single(e => e.Kind == EventModelElementKind.ReadModel).Lane.ShouldBe(EventModelLane.ReadModel);

        // ids are stable and unique within the slice, and carry the type identity for drift matching
        elements.Select(e => e.Id).Distinct().Count().ShouldBe(elements.Count);
        var command = elements.Single(e => e.Kind == EventModelElementKind.Command);
        command.Id.ShouldBe($"PlaceOrder/Command/{typeof(PlaceOrder).FullName}");
        command.Type.ShouldBe(T<PlaceOrder>());
        command.Label.ShouldBe(nameof(PlaceOrder));

        // directed edges, by id
        string id(EventModelElementKind kind) => elements.Single(e => e.Kind == kind).Id;
        slice.Edges.ShouldBe(new[]
        {
            new EventModelEdge(id(EventModelElementKind.Trigger), id(EventModelElementKind.Command)),
            new EventModelEdge(id(EventModelElementKind.Command), id(EventModelElementKind.Handler)),
            new EventModelEdge(id(EventModelElementKind.Handler), id(EventModelElementKind.Aggregate)),
            new EventModelEdge(id(EventModelElementKind.Handler), id(EventModelElementKind.Event)),
            new EventModelEdge(id(EventModelElementKind.Handler), id(EventModelElementKind.Message)),
            new EventModelEdge(id(EventModelElementKind.Message), id(EventModelElementKind.ExternalSystem)),
            new EventModelEdge(id(EventModelElementKind.Event), id(EventModelElementKind.Projection)),
            new EventModelEdge(id(EventModelElementKind.Projection), id(EventModelElementKind.ReadModel)),
        });

        // the rendering contract is computed, so it can never disagree with the typed roles
        (slice with { EmittedEvents = Array.Empty<TypeDescriptor>() }).Elements
            .ShouldNotContain(e => e.Kind == EventModelElementKind.Event);
    }

    [Fact]
    public void events_link_straight_to_read_models_when_there_is_no_projection()
    {
        var slice = EventModelSliceDescriptor.Named("x") with
        {
            EmittedEvents = new[] { T<OrderPlaced>() },
            ReadModelTypes = new[] { T<OrderSummary>() },
        };

        slice.Edges.Single().ShouldBe(new EventModelEdge(
            $"x/Event/{typeof(OrderPlaced).FullName}",
            $"x/ReadModel/{typeof(OrderSummary).FullName}"));
    }

    [Fact]
    public void inbound_external_system_is_the_trigger_of_a_translation_slice()
    {
        var slice = EventModelSliceDescriptor.Named("Import") with
        {
            Pattern = SlicePattern.Translation,
            TriggerKind = TriggerKind.External,
            HandlerType = T<OrderHandler>(),
            EmittedEvents = new[] { T<OrderPlaced>() },
            ExternalSystems = new[] { new ExternalSystemDescriptor("Legacy", ExternalSystemDirection.Inbound, "rabbitmq://queue/legacy") },
        };

        slice.Edges.ShouldBe(new[]
        {
            new EventModelEdge("Import/ExternalSystem/Legacy", $"Import/Handler/{typeof(OrderHandler).FullName}"),
            new EventModelEdge($"Import/Handler/{typeof(OrderHandler).FullName}", $"Import/Event/{typeof(OrderPlaced).FullName}"),
        });
    }

    [Fact]
    public void trigger_origin_label_stands_in_for_a_missing_trigger()
    {
        var slice = EventModelSliceDescriptor.Named("x") with
        {
            CommandType = T<PlaceOrder>(),
            TriggerKind = TriggerKind.Http,
            TriggerOrigin = new PublisherOrigin { HttpMethod = "POST", HttpRoute = "/orders", Label = "POST /orders" },
        };

        var trigger = slice.Elements.Single(e => e.Kind == EventModelElementKind.Trigger);
        trigger.Label.ShouldBe("POST /orders");
        trigger.Type.ShouldBeNull();
    }

    [Fact]
    public void every_element_kind_has_a_lane_and_a_palette_colour()
    {
        foreach (var kind in Enum.GetValues<EventModelElementKind>())
        {
            Enum.IsDefined(EventModelElement.LaneFor(kind)).ShouldBeTrue();
            EventModelPalette.ColorFor(kind).ShouldStartWith("#");
        }
    }

    [Fact]
    public void merge_lets_the_first_slice_win_scalars_and_unions_lists_by_identity()
    {
        var derived = new EventModelSliceDescriptor("PlaceOrder", null, null, T<PlaceOrder>(), T<OrderHandler>(),
                new[] { T<OrderPlaced>() }, Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>())
        {
            Pattern = SlicePattern.Command,
            TriggerKind = TriggerKind.Http,
            AggregateTypes = new[] { T<Order>() },
            Specifications = new[] { new SpecificationDescriptor("Place Order/Place an order", new[] { T<PlaceOrder>(), T<OrderPlaced>() }) },
        };

        var overlay = EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            TriggerLabel = "User clicks Place Order",
            Domain = "Orders",
            // the same spec linked by identity only — must not duplicate
            Specifications = new[] { new SpecificationDescriptor("Place Order/Place an order"), new SpecificationDescriptor("Place Order/Rejects empty cart") },
            EmittedEvents = new[] { T<OrderPlaced>(), T<OrderConfirmed>() },
            Hotspots = new[] { HotspotDescriptor.PendingSpecification("Place Order/Rejects empty cart") },
        };

        var merged = derived.Merge(overlay);

        merged.CommandType.ShouldBe(T<PlaceOrder>());
        merged.Pattern.ShouldBe(SlicePattern.Command);
        merged.TriggerKind.ShouldBe(TriggerKind.Http);
        merged.TriggerLabel.ShouldBe("User clicks Place Order");
        merged.Domain.ShouldBe("Orders");
        merged.EmittedEvents.ShouldBe(new[] { T<OrderPlaced>(), T<OrderConfirmed>() });
        merged.AggregateTypes.ShouldBe(new[] { T<Order>() });
        merged.Specifications.Select(x => x.Identity).ShouldBe(new[] { "Place Order/Place an order", "Place Order/Rejects empty cart" });
        // the derived spec (with resolved types) wins over the identity-only link
        merged.Specifications[0].ResolvedTypes.Count.ShouldBe(2);
        merged.Hotspots.Single().Origin.ShouldBe(HotspotOrigin.PendingSpecification);
    }

    [Fact]
    public void merge_refuses_a_different_slice()
    {
        Should.Throw<ArgumentException>(() =>
            EventModelSliceDescriptor.Named("a").Merge(EventModelSliceDescriptor.Named("b")));
    }

    [Fact]
    public void model_merge_folds_slices_by_name_across_descriptors_and_unions_aggregates()
    {
        var order = new AggregateDescriptor(T<Order>(), AggregateKind.WriteAggregate, new[] { T<OrderPlaced>() });

        var derived = new EventModelDescriptor("Orders", new[]
        {
            new EventModelSliceDescriptor("PlaceOrder", null, null, T<PlaceOrder>(), T<OrderHandler>(),
                new[] { T<OrderPlaced>() }, Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>()) { Pattern = SlicePattern.Command },
            EventModelSliceDescriptor.Named("ShipOrder") with { Pattern = SlicePattern.Command },
        }) { Aggregates = new[] { order } };

        var overlay = new EventModelDescriptor("Orders", new[]
        {
            EventModelSliceDescriptor.Named("ShipOrder") with { Domain = "Fulfilment" },
            EventModelSliceDescriptor.Named("PlaceOrder") with { Domain = "Orders" },
            EventModelSliceDescriptor.Named("CancelOrder") with { Domain = "Orders" },
        }) { Aggregates = new[] { order } };

        var merged = EventModelDescriptor.Merge("Orders", new[] { derived, overlay });

        merged.Name.ShouldBe("Orders");
        merged.Slices.Select(s => s.Name).ShouldBe(new[] { "PlaceOrder", "ShipOrder", "CancelOrder" });
        merged.Slices[0].CommandType.ShouldBe(T<PlaceOrder>());
        merged.Slices[0].Domain.ShouldBe("Orders");
        merged.Slices[1].Domain.ShouldBe("Fulfilment");
        merged.Slices[2].Pattern.ShouldBeNull(); // overlay-only slice — named, not derived
        merged.Aggregates.ShouldBe(new[] { order });
    }

    [Fact]
    public void model_descriptor_positional_shape_still_compiles_and_aggregates_default_empty()
    {
        new EventModelDescriptor("m", Array.Empty<EventModelSliceDescriptor>()).Aggregates.ShouldBeEmpty();
    }

    public class PlaceOrder { }
    public class OrderHandler { }
    public class Order { }
    public class OrderPlaced { }
    public class OrderConfirmed { }
    public class NotifyWarehouse { }
    public class OrderProjection { }
    public class OrderSummary { }
}
