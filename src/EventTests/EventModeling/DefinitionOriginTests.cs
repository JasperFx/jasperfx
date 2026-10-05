using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#959: a fluent definition names itself on its slices, so a disagreement says which model
/// declared the losing claim — and a declared origin never disagrees with another source's.
/// </summary>
public class DefinitionOriginTests
{
    private static async Task<EventModelDescriptor> declare(string name, Action<EventModelBuilder> configure)
    {
        IEventModelDefinitionSource source = EventModelDefinitionSource.For(name, configure);
        var descriptor = await source.TryCreateAsync(new ServiceCollection().BuildServiceProvider(), default);
        return descriptor!.WithProvenance(source.Provenance);
    }

    private static EventModelSliceDescriptor derived(string name) =>
        EventModelSliceDescriptor.Named(name) with
        {
            Pattern = SlicePattern.Command,
            Origin = new Uri("event-model://wolverine/"),
            Provenance = EventModelProvenance.Derived,
        };

    [Fact]
    public async Task every_declared_slice_carries_the_definition_as_its_origin()
    {
        var model = await declare("Helpdesk", b =>
        {
            b.Slice("LogIncident").TriggeredBy("Customer submits the incident form");
            b.Slice("CloseIncident");
        });

        model.Slices.Select(x => x.Origin).ShouldAllBe(x => x == new Uri("event-model://Helpdesk"));
    }

    [Fact]
    public async Task a_disagreement_against_a_declared_claim_names_the_definition()
    {
        var declared = (await declare("Helpdesk", b =>
            b.Slice("LogIncident").ForFlowNotOwnedHere(r => r.Pattern(SlicePattern.Automation)))).Slices.Single();

        var merged = declared.Merge(derived("LogIncident"));

        var hotspot = merged.Hotspots.Single(x => x.Origin == HotspotOrigin.SourceDisagreement);
        hotspot.Role.ShouldBe(EventModelRole.Pattern);
        hotspot.LosingClaim!.Source.ShouldBe("event-model://Helpdesk");
        hotspot.Text.ShouldBe("Pattern: event-model://wolverine/ claims Command; event-model://Helpdesk claims Automation");
    }

    [Fact]
    public async Task a_declared_origin_never_disagrees_with_the_code_behind_it()
    {
        var declared = (await declare("Helpdesk", b => b.Slice("LogIncident").InDomain("Incidents"))).Slices.Single();

        foreach (var merged in new[] { declared.Merge(derived("LogIncident")), derived("LogIncident").Merge(declared) })
        {
            // The code's origin stands, the declaration takes nothing away, and nothing is recorded.
            merged.Origin.ShouldBe(new Uri("event-model://wolverine/"));
            merged.Domain.ShouldBe("Incidents");
            merged.Hotspots.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task two_declared_sources_for_one_slice_are_normal_not_a_disagreement()
    {
        // Spec-first work routinely has a model AND its specs describing the same slice, both Declared.
        var model = (await declare("Helpdesk", b => b.Slice("LogIncident"))).Slices.Single();
        var specs = EventModelSliceDescriptor.Named("LogIncident") with
        {
            Origin = new Uri("event-model://Helpdesk.Specs"),
            Provenance = EventModelProvenance.Declared,
            Specifications = [new SpecificationDescriptor("Log Incident/Logs an incident")],
        };

        var merged = model.Merge(specs);

        merged.Origin.ShouldBe(new Uri("event-model://Helpdesk"));
        merged.Specifications.ShouldHaveSingleItem();
        merged.Hotspots.ShouldBeEmpty();
    }
}
