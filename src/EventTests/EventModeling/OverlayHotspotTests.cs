using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

// jasperfx#690: prose hotspots declared through the overlay. The overlay still declares no
// roles — a hotspot is an annotation, the one thing about an unfinished model that no source
// can derive, because nobody has written the code or the spec that would give it away yet.
public class OverlayHotspotTests
{
    [Fact]
    public void a_slice_hotspot_lands_on_that_slice_as_prose()
    {
        var builder = new EventModelBuilder();
        builder.Slice("CloseIncident")
            .Hotspot("Can an incident be closed while a customer response is outstanding?");

        var hotspot = builder.BuildSlices().Single().Hotspots.Single();
        hotspot.Origin.ShouldBe(HotspotOrigin.Prose);
        hotspot.Text.ShouldBe("Can an incident be closed while a customer response is outstanding?");
        hotspot.SpecificationIdentity.ShouldBeNull();
    }

    [Fact]
    public void slice_hotspots_keep_declaration_order_and_chain_with_the_rest_of_the_overlay()
    {
        var builder = new EventModelBuilder();
        builder.Slice("CloseIncident")
            .InDomain("Helpdesk")
            .TriggeredBy("Agent clicks Close")
            .Hotspot("first")
            .LinksToSpecification("Close Incident/Closes a resolved incident")
            .Hotspot("second");

        var slice = builder.BuildSlices().Single();
        slice.Domain.ShouldBe("Helpdesk");
        slice.TriggerLabel.ShouldBe("Agent clicks Close");
        slice.Specifications.Single().Identity.ShouldBe("Close Incident/Closes a resolved incident");
        slice.Hotspots.Select(x => x.Text).ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public void a_model_hotspot_lands_on_the_model_and_not_on_any_slice()
    {
        var builder = new EventModelBuilder { Name = "Helpdesk" };
        builder.Hotspot("Who owns the SLA clock once an incident is escalated?");
        builder.Slice("CloseIncident");

        var model = builder.Build("fallback");
        model.Hotspots.Single().Text.ShouldBe("Who owns the SLA clock once an incident is escalated?");
        model.Hotspots.Single().Origin.ShouldBe(HotspotOrigin.Prose);
        model.Slices.Single().Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void model_hotspots_keep_declaration_order()
    {
        var builder = new EventModelBuilder();
        builder.Hotspot("first").Hotspot("second");

        builder.Build("m").Hotspots.Select(x => x.Text).ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public void a_model_with_no_hotspots_declared_has_none()
    {
        var builder = new EventModelBuilder();
        builder.Slice("CloseIncident");

        var model = builder.Build("m");
        model.Hotspots.ShouldBeEmpty();
        model.Slices.Single().Hotspots.ShouldBeEmpty();
    }

    // The overlay is merged ONTO the derived slice, so an overlay hotspot has to survive the
    // fold next to whatever the binding source already stamped there.
    [Fact]
    public void an_overlay_hotspot_survives_the_merge_next_to_a_derived_pending_spec_hotspot()
    {
        var derived = EventModelSliceDescriptor.Named("CloseIncident") with
        {
            CommandType = TypeDescriptor.For(typeof(CloseIncident)),
            Hotspots = new[] { HotspotDescriptor.PendingSpecification("Close Incident/Rejects an open incident") },
        };

        var builder = new EventModelBuilder();
        builder.Slice("CloseIncident").Hotspot("Do we notify the customer on close?");
        var overlay = builder.BuildSlices().Single();

        var merged = derived.Merge(overlay);

        merged.CommandType.ShouldBe(TypeDescriptor.For(typeof(CloseIncident)));
        merged.Hotspots.Select(x => (x.Origin, x.Text)).ShouldBe(new[]
        {
            (HotspotOrigin.PendingSpecification, "Close Incident/Rejects an open incident"),
            (HotspotOrigin.Prose, "Do we notify the customer on close?"),
        });
    }

    [Fact]
    public void the_same_prose_hotspot_from_two_sources_is_folded_into_one()
    {
        var slice = EventModelSliceDescriptor.Named("CloseIncident") with
        {
            Hotspots = new[] { HotspotDescriptor.Prose("Do we notify the customer on close?") },
        };

        slice.Merge(slice with { }).Hotspots.Count.ShouldBe(1);
    }

    [Fact]
    public void prose_and_a_pending_spec_with_the_same_text_are_not_folded_together()
    {
        var slice = EventModelSliceDescriptor.Named("CloseIncident") with
        {
            Hotspots = new[] { HotspotDescriptor.Prose("Close Incident/Rejects an open incident") },
        };

        var other = EventModelSliceDescriptor.Named("CloseIncident") with
        {
            Hotspots = new[] { HotspotDescriptor.PendingSpecification("Close Incident/Rejects an open incident") },
        };

        slice.Merge(other).Hotspots.Count.ShouldBe(2);
    }

    [Fact]
    public void model_level_hotspots_are_unioned_across_sources_and_deduplicated()
    {
        var derived = new EventModelDescriptor("Helpdesk", Array.Empty<EventModelSliceDescriptor>())
        {
            Hotspots = new[] { HotspotDescriptor.Prose("shared") },
        };

        var builder = new EventModelBuilder { Name = "Helpdesk" };
        builder.Hotspot("shared").Hotspot("overlay only");

        var merged = EventModelDescriptor.Merge("Helpdesk", new[] { derived, builder.Build("Helpdesk") });

        merged.Hotspots.Select(x => x.Text).ShouldBe(new[] { "shared", "overlay only" });
    }

    // The whole point of declaring a hotspot: an unfinished model still renders, and the
    // unfinished part is visible on the canvas rather than in someone's head.
    [Fact]
    public void a_slice_that_is_nothing_but_a_hotspot_still_renders_one_element()
    {
        var builder = new EventModelBuilder();
        builder.Slice("EscalateIncident")
            .Hotspot("No idea yet what escalation does to the SLA clock");

        var slice = builder.BuildSlices().Single();
        var element = slice.Elements.Single();
        element.Kind.ShouldBe(EventModelElementKind.Hotspot);
        element.Lane.ShouldBe(EventModelLane.Wireframe);
        element.Label.ShouldBe("No idea yet what escalation does to the SLA clock");
        slice.Edges.ShouldBeEmpty();
    }

    public class CloseIncident { }
}
