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
    public void prose_is_the_reserved_form_with_no_spec_identity()
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
}
