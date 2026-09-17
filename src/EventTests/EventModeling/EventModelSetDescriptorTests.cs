using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#837 — a service and its Event Models are a <em>pair</em>. Discovery already assembled one
/// model per name; the exporters folded them straight back into one named for the service, so a
/// modular monolith lost a model name outright and had its slices merged into another model's.
/// </summary>
public class EventModelSetDescriptorTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static EventModelDescriptor model(string name, params string[] sliceNames)
        => new(name, sliceNames.Select(EventModelSliceDescriptor.Named).ToList());

    #region the shape

    /// <summary>
    /// <b>The acceptance criterion.</b> Two modules naming their own model produce two models under
    /// one service, and both names survive.
    /// </summary>
    [Fact]
    public void a_service_carries_every_model_it_hosts()
    {
        var set = EventModelSetDescriptor.For("Billing",
            [model("HelpDesk", "CloseIncident"), model("Incidents", "CloseIncident")]);

        set.ServiceName.ShouldBe("Billing");
        set.Models.Select(x => x.Name).ShouldBe(["HelpDesk", "Incidents"]);

        // ...and the two identically-named commands stayed two slices, which is the whole point: they
        // are not one slice, they are one name in two bounded contexts.
        set.Models[0].Slices.ShouldHaveSingleItem().Name.ShouldBe("CloseIncident");
        set.Models[1].Slices.ShouldHaveSingleItem().Name.ShouldBe("CloseIncident");
    }

    /// <summary>
    /// Several sources describing <em>one</em> model still fold into one, exactly as before — the
    /// grouping is per name, and the name is what decides whether two descriptors are one model.
    /// </summary>
    [Fact]
    public void several_sources_describing_one_model_still_fold_into_one()
    {
        var set = EventModelSetDescriptor.For("Billing",
        [
            new EventModelDescriptor("Orders", [EventModelSliceDescriptor.Named("PlaceOrder") with { Domain = "Orders" }]),
            new EventModelDescriptor("Orders",
            [
                EventModelSliceDescriptor.Named("PlaceOrder") with
                {
                    Provenance = EventModelProvenance.Derived, CommandType = T<PlaceOrder>(),
                },
            ]),
        ]);

        var slice = set.Models.ShouldHaveSingleItem().Slices.ShouldHaveSingleItem();
        slice.Domain.ShouldBe("Orders");
        slice.CommandType.ShouldBe(T<PlaceOrder>());
    }

    [Fact]
    public void a_set_with_no_models_is_legal()
    {
        var set = EventModelSetDescriptor.For("Billing", []);

        set.Models.ShouldBeEmpty();
        set.Sole.ShouldBeNull();
        set.IsAmbiguous.ShouldBeFalse();
        set.Find("Orders").ShouldBeNull();
    }

    #endregion

    #region the caller chooses

    /// <summary>
    /// Where a single descriptor is genuinely required, the caller names the model it wants rather
    /// than having "first non-default name wins" choose for it.
    /// </summary>
    [Fact]
    public void the_caller_picks_the_model_by_name()
    {
        var set = EventModelSetDescriptor.For("Billing", [model("HelpDesk"), model("Incidents")]);

        set.Find("Incidents").ShouldNotBeNull().Name.ShouldBe("Incidents");
        set.Find("Nothing").ShouldBeNull();
    }

    /// <summary>
    /// <see cref="EventModelSetDescriptor.Sole" /> is the honest form of the old assumption: an
    /// answer when the service really does host one model, and a null rather than a guess when it
    /// does not.
    /// </summary>
    [Fact]
    public void sole_answers_only_when_there_is_exactly_one_model()
    {
        EventModelSetDescriptor.For("Billing", [model("Orders")]).Sole.ShouldNotBeNull().Name.ShouldBe("Orders");

        var several = EventModelSetDescriptor.For("Billing", [model("HelpDesk"), model("Incidents")]);
        several.Sole.ShouldBeNull();
        several.IsAmbiguous.ShouldBeTrue();
    }

    #endregion

    #region collapsing is explicit and recorded

    /// <summary>
    /// A consumer whose wire cannot carry more than one model yet can still fold — but the fold is
    /// the caller's choice and leaves a <see cref="HotspotOrigin.ModelCollapse" /> hotspot naming
    /// every model that went in, where the exporters used to report nothing at all.
    /// </summary>
    [Fact]
    public void collapsing_several_models_records_what_was_folded()
    {
        var collapsed = EventModelSetDescriptor
            .For("Billing", [model("HelpDesk", "CloseIncident"), model("Incidents", "RaiseIncident")])
            .Collapse();

        collapsed.Name.ShouldBe("Billing");
        collapsed.Slices.Select(x => x.Name).ShouldBe(["CloseIncident", "RaiseIncident"]);

        var hotspot = collapsed.Hotspots.ShouldHaveSingleItem();
        hotspot.Origin.ShouldBe(HotspotOrigin.ModelCollapse);
        hotspot.Text.ShouldBe(
            "Billing hosts several Event Models and they were collapsed into one: HelpDesk, Incidents");

        // jasperfx#853: and the same facts in the form a consumer can act on -- offer a picker,
        // route per model, count them -- rather than only inside the sentence.
        hotspot.ServiceName.ShouldBe("Billing");
        hotspot.CollapsedModelNames.ShouldBe(["HelpDesk", "Incidents"]);
    }

    /// <summary>
    /// jasperfx#853: the structured half is not a second answer to a different question — it is the
    /// same models the prose names, in the same order.
    /// </summary>
    [Fact]
    public void the_structured_form_and_the_prose_name_the_same_models()
    {
        var hotspot = EventModelSetDescriptor
            .For("Billing", [model("HelpDesk"), model("Incidents"), model("Orders")])
            .Collapse()
            .Hotspots.ShouldHaveSingleItem();

        hotspot.Text.ShouldEndWith(string.Join(", ", hotspot.CollapsedModelNames));
        hotspot.Text.ShouldStartWith(hotspot.ServiceName!);
    }

    /// <summary>
    /// The structured members belong to <see cref="HotspotOrigin.ModelCollapse" /> alone, exactly as
    /// <see cref="HotspotDescriptor.WinningClaim" /> belongs to a source disagreement alone.
    /// </summary>
    [Fact]
    public void the_other_origins_carry_no_collapse_detail()
    {
        foreach (var hotspot in new[]
                 {
                     HotspotDescriptor.Prose("Who owns the SLA clock?"),
                     HotspotDescriptor.PendingSpecification("Close Incident/Rejects an open incident"),
                 })
        {
            hotspot.ServiceName.ShouldBeNull();
            hotspot.CollapsedModelNames.ShouldBeEmpty();
        }
    }

    /// <summary>
    /// Collapsing one model loses nothing, so it records nothing — a service that really does host a
    /// single model produces exactly what it produced before jasperfx#837.
    /// </summary>
    [Fact]
    public void collapsing_one_model_records_nothing()
    {
        var collapsed = EventModelSetDescriptor.For("Billing", [model("Orders", "PlaceOrder")]).Collapse();

        collapsed.Name.ShouldBe("Billing");
        collapsed.Slices.ShouldHaveSingleItem().Name.ShouldBe("PlaceOrder");
        collapsed.Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void a_collapse_can_be_named()
    {
        EventModelSetDescriptor.For("Billing", [model("Orders")]).Collapse("Everything").Name.ShouldBe("Everything");
    }

    #endregion

    #region discovery

    /// <summary>
    /// The discovery path produces the set directly, so an exporter never has to reach for
    /// <see cref="EventModelDescriptor.Merge" /> across models that are not one model.
    /// </summary>
    [Fact]
    public async Task assemble_set_carries_the_service_and_every_model_under_it()
    {
        var services = new ServiceCollection()
            .AddEventModel("HelpDesk", m => m.Slice("CloseIncident"))
            .AddEventModel("Incidents", m => m.Slice("CloseIncident"))
            .BuildServiceProvider();

        var set = EventModelSetDescriptor
            .For("Billing", await EventModelDiscovery.DiscoverAsync(services, TestContext.Current.CancellationToken));

        set.ServiceName.ShouldBe("Billing");
        set.Models.Select(x => x.Name).ShouldBe(["HelpDesk", "Incidents"]);

        // ...and the shorthand that does both
        var direct = await EventModelDiscovery.AssembleSetAsync(services, "Billing", TestContext.Current.CancellationToken);
        direct.Models.Select(x => x.Name).ShouldBe(["HelpDesk", "Incidents"]);
    }

    /// <summary>
    /// <see cref="EventModelDiscovery.Assemble" /> is unchanged — one model per name, in
    /// first-appearance order. It was always right; the exporters were not.
    /// </summary>
    [Fact]
    public void assemble_and_the_set_group_identically()
    {
        EventModelDescriptor[] descriptors =
            [model("HelpDesk"), model("Incidents"), model("HelpDesk", "CloseIncident")];

        EventModelDiscovery.Assemble(descriptors).Select(x => x.Name).ShouldBe(["HelpDesk", "Incidents"]);
        EventModelSetDescriptor.For("Billing", descriptors).Models.Select(x => x.Name)
            .ShouldBe(["HelpDesk", "Incidents"]);
    }

    #endregion

    public class PlaceOrder { }
}
