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

    #region equality survives the one collection member (jasperfx#853)

    /// <summary>
    /// <see cref="HotspotDescriptor.CollapsedModelNames" /> is a collection, and a record's generated
    /// equality compares a collection by reference — so this is the assertion that says the type's
    /// equality still means what the rest of it promises.
    /// </summary>
    [Fact]
    public void two_collapses_naming_the_same_models_are_equal()
    {
        var first = HotspotDescriptor.ModelCollapse("Billing", new[] { "HelpDesk", "Incidents" });
        var second = HotspotDescriptor.ModelCollapse("Billing", new List<string> { "HelpDesk", "Incidents" });

        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());

        // ...and a set de-duplicates them, which is what a merge folding two exports needs.
        new HashSet<HotspotDescriptor> { first, second }.Count.ShouldBe(1);
    }

    [Fact]
    public void collapses_naming_different_models_are_not_equal()
    {
        HotspotDescriptor.ModelCollapse("Billing", new[] { "HelpDesk", "Incidents" })
            .ShouldNotBe(HotspotDescriptor.ModelCollapse("Billing", new[] { "HelpDesk", "Orders" }));

        // Order is part of the answer -- the names come back in the order they were folded.
        HotspotDescriptor.ModelCollapse("Billing", new[] { "HelpDesk", "Incidents" })
            .ShouldNotBe(HotspotDescriptor.ModelCollapse("Billing", new[] { "Incidents", "HelpDesk" }));
    }

    [Fact]
    public void the_other_members_are_still_part_of_equality()
    {
        var prose = HotspotDescriptor.Prose("Who owns the SLA clock?");

        prose.ShouldBe(HotspotDescriptor.Prose("Who owns the SLA clock?"));
        prose.ShouldNotBe(HotspotDescriptor.Prose("Who owns the retention window?"));
        prose.ShouldNotBe(prose with { Origin = HotspotOrigin.ModelCollapse });
        prose.ShouldNotBe(prose with { SpecificationIdentity = "Close Incident/Rejects" });
        prose.ShouldNotBe(prose with { Role = EventModelRole.EmittedEvents });
        prose.ShouldNotBe(prose with { ServiceName = "Billing" });
        prose.ShouldNotBe(prose with
        {
            WinningClaim = new EventModelClaim(EventModelProvenance.Observed, "OrderPlaced"),
        });
        prose.ShouldNotBe(prose with
        {
            LosingClaim = new EventModelClaim(EventModelProvenance.Derived, "OrderPlaced"),
        });
    }

    #endregion
}
