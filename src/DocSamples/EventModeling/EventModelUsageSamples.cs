using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;

namespace DocSamples.EventModeling;

public class EventModelUsageSamples
{
    public void register_the_overlay(IServiceCollection services)
    {
        #region sample_registering_an_event_model

        // One definition at a time
        services.AddEventModel<IncidentServiceModel>();
        services.AddEventModel<IncidentServiceHotspots>();

        // ...or every EventModelDefinition in an assembly
        services.AddEventModelsFromAssembly(typeof(IncidentServiceModel).Assembly);

        #endregion
    }

    public void register_an_inline_overlay(IServiceCollection services)
    {
        #region sample_registering_an_inline_event_model

        services.AddEventModel("Helpdesk", model =>
        {
            model.InDomain("Incidents");

            model.Slice("CloseIncident")
                .TriggeredBy("Agent clicks Close")
                .Hotspot("Can an incident be closed before the customer acknowledges the resolution?");
        });

        #endregion
    }

    public async Task assemble_the_model(IServiceProvider services)
    {
        #region sample_assembling_the_event_model

        // Ask every registered source — Wolverine's chains, the Bobcat generator, your
        // overlays — for its view, then fold them into one descriptor per model name
        var models = await EventModelDiscovery.AssembleAsync(services);

        var helpdesk = models.Single(x => x.Name == "Helpdesk");

        foreach (var slice in helpdesk.Slices)
        {
            Console.WriteLine($"{slice.Domain}/{slice.Name}: {slice.Pattern}");

            foreach (var hotspot in slice.Hotspots)
            {
                Console.WriteLine($"  ⚠ {hotspot.Origin}: {hotspot.Text}");
            }
        }

        // Questions that belong to the model rather than to one slice
        foreach (var hotspot in helpdesk.Hotspots)
        {
            Console.WriteLine($"⚠ {hotspot.Text}");
        }

        #endregion
    }

    public void what_a_derived_source_stamps()
    {
        #region sample_a_derived_slice

        // This is what a source builds — Wolverine reading its own HTTP chain for
        // CloseIncidentEndpoint. You never hand-write this; it is here so you can see
        // exactly which slots the overlay is *not* allowed to fill.
        var derived = new EventModelSliceDescriptor(
            "CloseIncident",
            TriggerLabel: null,
            TriggerType: null,
            CommandType: TypeDescriptor.For(typeof(CloseIncident)),
            HandlerType: TypeDescriptor.For(typeof(CloseIncidentEndpoint)),
            EmittedEvents: [TypeDescriptor.For(typeof(IncidentClosed))],
            ProjectionTypes: [],
            ReadModelTypes: [TypeDescriptor.For(typeof(Incident))])
        {
            Pattern = SlicePattern.Command,
            TriggerKind = TriggerKind.Http,
            TriggerOrigin = new PublisherOrigin
            {
                HttpMethod = "POST",
                HttpRoute = "/api/incidents/close/{id}",
                Label = "POST /api/incidents/close/{id}"
            },
            AggregateTypes = [TypeDescriptor.For(typeof(Incident))],
            PublishedMessages = [TypeDescriptor.For(typeof(ArchiveIncident))]
        };

        #endregion

        #region sample_merging_the_overlay_onto_a_derived_slice

        var builder = new EventModelBuilder();
        builder.Slice("CloseIncident")
            .InDomain("Incidents")
            .TriggeredBy("Agent clicks Close")
            .Hotspot("Can an incident be closed before the customer acknowledges the resolution?");

        var overlay = builder.BuildSlices().Single();

        // Derived first: scalars keep the first non-null value, so a derived role always wins
        var merged = derived.Merge(overlay);

        Console.WriteLine(merged.CommandType!.Name);   // CloseIncident — from the chain
        Console.WriteLine(merged.TriggerLabel);        // Agent clicks Close — from the overlay
        Console.WriteLine(merged.Hotspots.Count);      // 1 — from the overlay

        #endregion
    }

    public void read_the_rendering_contract(EventModelSliceDescriptor slice)
    {
        #region sample_reading_the_rendering_contract

        // Elements and Edges are computed from the typed roles on every read, so a viewer
        // draws straight from the descriptor with no second transform
        foreach (var element in slice.Elements)
        {
            Console.WriteLine($"{element.Lane,-12} {element.Kind,-15} {element.Label} " +
                              $"({EventModelPalette.ColorFor(element.Kind)})");
        }

        foreach (var edge in slice.Edges)
        {
            Console.WriteLine($"{edge.FromId} -> {edge.ToId}");
        }

        #endregion
    }

    public void serialize_for_a_viewer(EventModelDescriptor model)
    {
        #region sample_serializing_an_event_model

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var json = JsonSerializer.Serialize(model, options);

        #endregion

        Console.WriteLine(json);
    }
}
