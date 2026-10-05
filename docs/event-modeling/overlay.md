# The Overlay

The overlay is the part of an Event Model you write by hand that **names, groups, annotates, links and flags** slices -- the things code can never tell you. This page covers those builder methods.

::: tip
The same builder can also declare a slice's *roles* (its command, aggregates, events and so on) for design-first work before the code exists. That's covered in [Declaring the Model in Code](/event-modeling/declaring).
:::

## `EventModelDefinition`

Derive from `EventModelDefinition` and override `Configure`:

<!-- snippet: sample_incident_service_overlay -->
<a id='snippet-sample_incident_service_overlay'></a>
```cs
public class IncidentServiceModel : EventModelDefinition
{
    // Every source that contributes to "Helpdesk" is folded into one model under this name
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        builder.InDomain("Incidents");

        builder.Slice("LogIncident")
            .TriggeredBy("Customer submits the incident form")
            .LinksToSpecification("Log Incident/Logs an incident from the web form");

        builder.Slice("CategoriseIncident")
            .TriggeredBy("Agent picks a category");

        builder.Slice("CloseIncident")
            .TriggeredBy("Agent clicks Close");

        builder.Slice("ArchiveIncident")
            .InDomain("Retention")
            .TriggeredBy("Three days after close");

        builder.Slice("GetIncident")
            .TriggeredBy("Agent opens the incident detail screen");
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/IncidentServiceEventModel.cs#L5-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_incident_service_overlay' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Name` is the model this overlay contributes to. Several definitions can return the same name and they are all folded into one model — which is how IncidentService keeps its slice names in one class and its open questions in another. Leave `Name` alone and it defaults to the class name.

## Slice names are the merge key

`builder.Slice("CloseIncident")` does not *create* a slice. It opens an overlay for a slice, keyed by name, that is later merged onto whatever a derived source stamped under that same name. By convention the name is the command's short name, because that is what Wolverine names its own slice.

Get the name wrong and nothing breaks loudly — you simply get two slices on the canvas, one derived and one bare. That is usually how you notice.

## The builder methods

### `InDomain(string)`

Groups slices into a bounded context so a large model can collapse into sub-diagrams. On `EventModelBuilder` it is a running default that applies to every slice opened *after* the call; on a slice it overrides that default:

```csharp
builder.InDomain("Incidents");

builder.Slice("LogIncident");                       // Incidents
builder.Slice("CategoriseIncident");                // Incidents
builder.Slice("ArchiveIncident").InDomain("Retention");  // Retention
```

### `TriggeredBy(string)`

A human-readable label for what starts the slice — "Agent clicks Close", "Customer submits the incident form". This is the one thing about a trigger that code genuinely cannot express. The trigger's *kind* (`Http`, `Grpc`, `MessageHandler`, `JobScheduler`, `Human`, `External`) and its CLR type are derived; only the sentence is yours.

### `LinksToSpecification(string)`

Binds a specification to the slice by its `{Feature}/{Scenario}` identity.

Use this **only** for a specification the binding source cannot see for itself — a manual test plan, a partner's acceptance suite, something outside the compilation. Specs the Bobcat generator or a code-first runner can see are bound by them, with their step types resolved, and re-typing them here would just be a second copy waiting to go stale.

### `Hotspot(string)`

Records an open question. See **[Hotspots](/event-modeling/hotspots)**.

## Registration

Every overlay is surfaced as an `IEventModelDefinitionSource` singleton:

<!-- snippet: sample_registering_an_event_model -->
<a id='snippet-sample_registering_an_event_model'></a>
```cs
// One definition at a time
services.AddEventModel<IncidentServiceModel>();
services.AddEventModel<IncidentServiceHotspots>();

// ...or every EventModelDefinition in an assembly
services.AddEventModelsFromAssembly(typeof(IncidentServiceModel).Assembly);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L13-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_an_event_model' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning AOT
`AddEventModelsFromAssembly` walks `Assembly.ExportedTypes` and is marked `[RequiresUnreferencedCode]`. Applications publishing native AOT should register each definition explicitly with `AddEventModel<T>()`.
:::

For something small, skip the class entirely:

<!-- snippet: sample_registering_an_inline_event_model -->
<a id='snippet-sample_registering_an_inline_event_model'></a>
```cs
services.AddEventModel("Helpdesk", model =>
{
    model.InDomain("Incidents");

    model.Slice("CloseIncident")
        .TriggeredBy("Agent clicks Close")
        .Hotspot("Can an incident be closed before the customer acknowledges the resolution?");
});
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L27-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_an_inline_event_model' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Definitions registered by type are resolved from the container at discovery time, or constructed through `ActivatorUtilities` if they are not registered — so a definition can take constructor dependencies (a feature-flag service, configuration) and still be discovered.

## The escape hatch

Sometimes a flow belongs on the model but its code is not yours: a partner system's command, a legacy service with no Wolverine chain to read. `ForFlowNotOwnedHere` is the explicitly-documented way to declare roles for exactly those flows.

IncidentService pushing closures out to a CRM it does not own:

<!-- snippet: sample_incident_service_external_flow -->
<a id='snippet-sample_incident_service_external_flow'></a>
```cs
public class CrmNotification : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        builder.Slice("NotifyCrmOfClosure")
            .InDomain("Integrations")
            .TriggeredBy("Incident closed")
            .ForFlowNotOwnedHere(roles => roles
                .Pattern(SlicePattern.Translation)
                .TriggeredBy(TriggerKind.MessageHandler)
                .UsesAggregate<Incident>()
                .ExternalSystem("Salesforce", ExternalSystemDirection.Outbound, "rabbitmq://queue/crm-updates"));
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/IncidentServiceEventModel.cs#L81-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_incident_service_external_flow' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
For flows your own code implements, declare the roles directly with the [role methods](/event-modeling/declaring#declaring-roles) instead. Those sit on the same rung, and once the code exists the derived roles take over and any difference shows up as a hotspot. `ForFlowNotOwnedHere` stays the clearest way to say "no source is ever going to derive this one".
:::

## Why the code always wins

Every source sits on a rung of the [provenance ladder](/event-modeling/descriptors#provenance-decides-the-merge), and for each role the higher rung's claim wins regardless of the order the sources were registered in. So if Wolverine stamped `CommandType = CloseIncident` and your overlay also declared one, Wolverine's wins:

<!-- snippet: sample_a_derived_slice -->
<a id='snippet-sample_a_derived_slice'></a>
```cs
// This is what a source builds -- Wolverine reading its own HTTP chain for
// CloseIncidentEndpoint. You never hand-write this; it is here so you can see
// exactly which slots the code fills in.
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
    PublishedMessages = [TypeDescriptor.For(typeof(ArchiveIncident))],
    Provenance = EventModelProvenance.Derived
};
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L72-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_a_derived_slice' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_merging_the_overlay_onto_a_derived_slice -->
<a id='snippet-sample_merging_the_overlay_onto_a_derived_slice'></a>
```cs
var builder = new EventModelBuilder();
builder.Slice("CloseIncident")
    .InDomain("Incidents")
    .TriggeredBy("Agent clicks Close")
    .Hotspot("Can an incident be closed before the customer acknowledges the resolution?");

var overlay = builder.BuildSlices().Single();

// The derived rung outranks the declared one, whichever order the merge runs in
var merged = overlay.Merge(derived);

Console.WriteLine(merged.CommandType!.Name);   // CloseIncident -- from the chain
Console.WriteLine(merged.TriggerLabel);        // Agent clicks Close -- from the overlay
Console.WriteLine(merged.Hotspots.Count);      // 1 -- from the overlay
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L102-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_merging_the_overlay_onto_a_derived_slice' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Hotspots always union, so an overlay hotspot lands next to a derived one instead of displacing it. For every other role, the highest rung that claims it wins outright, and if a lower rung claimed something different, that difference is recorded as a [source disagreement](/event-modeling/hotspots#a-source-disagreement-is-a-hotspot).
