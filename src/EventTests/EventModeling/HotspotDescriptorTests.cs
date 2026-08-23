using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

// jasperfx#689: a pending specification is a hotspot.
public class HotspotDescriptorTests
{
    [Fact]
    public void pending_specification_carries_the_spec_identity_as_origin_and_text()
    {
        var hotspot = HotspotDescriptor.PendingSpecification("Place Order/Rejects empty cart");

        hotspot.Origin.ShouldBe(HotspotOrigin.PendingSpecification);
        hotspot.SpecificationIdentity.ShouldBe("Place Order/Rejects empty cart");
        hotspot.Text.ShouldBe("Place Order/Rejects empty cart");
    }

    [Fact]
    public void prose_carries_the_note_itself_and_no_spec_identity()
    {
        var hotspot = HotspotDescriptor.Prose("Refund policy unclear");

        hotspot.Origin.ShouldBe(HotspotOrigin.Prose);
        hotspot.SpecificationIdentity.ShouldBeNull();
        hotspot.Text.ShouldBe("Refund policy unclear");
    }

    [Fact]
    public void a_pending_spec_hotspot_renders_on_the_slice_it_binds_to_in_the_hotspot_colour()
    {
        var slice = EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            Specifications = new[] { new SpecificationDescriptor("Place Order/Rejects empty cart") },
            Hotspots = new[] { HotspotDescriptor.PendingSpecification("Place Order/Rejects empty cart") },
        };

        var element = slice.Elements.Single(e => e.Kind == EventModelElementKind.Hotspot);
        element.Id.ShouldBe("PlaceOrder/Hotspot/Place Order/Rejects empty cart");
        element.Lane.ShouldBe(EventModelLane.Wireframe);
        element.Type.ShouldBeNull();
        EventModelPalette.ColorFor(element.Kind).ShouldBe("#E91E63");
    }

    // jasperfx#690: a prose hotspot renders exactly like a pending-spec one — same kind, same
    // lane, same colour. Only the text and the missing spec identity tell them apart.
    [Fact]
    public void a_prose_hotspot_renders_the_same_way_a_pending_spec_one_does()
    {
        var slice = EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            Hotspots = new[] { HotspotDescriptor.Prose("Refund policy unclear when partially shipped") },
        };

        var element = slice.Elements.Single(e => e.Kind == EventModelElementKind.Hotspot);
        element.Id.ShouldBe("PlaceOrder/Hotspot/Refund policy unclear when partially shipped");
        element.Label.ShouldBe("Refund policy unclear when partially shipped");
        element.Lane.ShouldBe(EventModelLane.Wireframe);
        EventModelPalette.ColorFor(element.Kind).ShouldBe("#E91E63");
    }
}
