# Hotspots

A **hotspot** is the model admitting it does not know something yet. On a whiteboard it is the pink sticky note with a question mark on it; on a JasperFx Event Model it is a `HotspotDescriptor`, rendered in the wireframe lane in the canonical hotspot magenta (`#E91E63`).

Hotspots come from two places, and the difference between them matters more than it looks.

## A pending specification is a hotspot

This is the primary mechanism, and the one you should reach for first.

When a slice has a specification bound to it that cannot pass yet — no bound steps, or steps that fail — the binding source stamps a hotspot on that slice automatically. Nobody writes it, and nobody has to remember to delete it: the day the spec passes, the hotspot is gone.

In IncidentService, the sample has no `ResolveIncident` slice yet. Rather than write a note about it:

<!-- snippet: sample_incident_service_pending_spec_hotspot -->
<a id='snippet-sample_incident_service_pending_spec_hotspot'></a>
```cs
public class ResolutionSlices : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        builder.Slice("ResolveIncident")
            // The question is sharp enough to name a scenario, so name it. The spec is
            // pending, and a pending spec is a hotspot — one that retires itself the day
            // the spec passes, which a prose note never does.
            .TriggeredBy("Agent clicks Resolve")
            .LinksToSpecification("Resolve Incident/An agent resolves a pending incident");
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/IncidentServiceEventModel.cs#L62-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_incident_service_pending_spec_hotspot' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The scenario named there does not exist yet either. That is the point — the model shows an unbuilt slice with an open question on it, and the question closes itself when someone writes the code that makes the scenario pass.

## Prose hotspots

Sometimes there is nothing to write a specification *against* yet. The question is real, but it is not yet sharp enough to name a scenario — you do not know the rule, so you cannot write the test.

`Hotspot("…")` puts that question on the canvas:

<!-- snippet: sample_incident_service_hotspots -->
<a id='snippet-sample_incident_service_hotspots'></a>
```cs
public class IncidentServiceHotspots : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        // Not about any one slice — nobody has decided this yet
        builder.Hotspot("Do we own the SLA clock, or does the CRM?");

        builder.Slice("CloseIncident")
            // Both of these rules are sitting commented out in CloseIncidentEndpoint.
            // Written down here, they show up on the canvas instead of in a code comment
            // nobody outside the team will ever read.
            .Hotspot("Can an incident be closed before the customer acknowledges the resolution?")
            .Hotspot("What happens to an outstanding response to the customer when we close?");

        builder.Slice("ArchiveIncident")
            .Hotspot("Three days is a guess. Ask legal what retention actually requires.");
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/IncidentServiceEventModel.cs#L37-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_incident_service_hotspots' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Those first two are not invented. Open `CloseIncidentEndpoint` in the Wolverine sample and you will find both rules sitting in a commented-out block:

```csharp
/* More logic for later
if (current.Status is not IncidentStatus.ResolutionAcknowledgedByCustomer)
    throw new InvalidOperationException("Only incident with acknowledged resolution can be closed");

if (current.HasOutstandingResponseToCustomer)
    throw new InvalidOperationException("Cannot close incident that has outstanding responses to customer");
*/
```

That is a hotspot in its natural habitat: a real, undecided rule, parked in a comment where only somebody already reading that file will ever see it. Declared in the overlay, the same two questions appear on the canvas in front of whoever is looking at the model — including the people who would actually know the answer.

## Slice-level or model-level

`Hotspot` exists on both builders and they mean different things:

| Call | Attaches to | Use when |
|------|-------------|----------|
| `builder.Slice("X").Hotspot("…")` | That slice | The question is about one thing the system does |
| `builder.Hotspot("…")` | The whole model | The question spans the flow, or is not about any one slice |

A slice hotspot renders as an element in that slice's wireframe lane. A model hotspot lands on `EventModelDescriptor.Hotspots` and is rendered by the viewer wherever it shows model-level notes.

A slice that is *nothing but* a hotspot still renders one element — which is exactly what you want for a slice you have thought about but not built.

## A source disagreement is a hotspot

The third origin is the only one you never write. When two sources describe the same slice and make **different** claims about the same role, the merge keeps one and records the other as a `SourceDisagreement` hotspot:

> ⚠ `EmittedEvents: Observed claims OrderPlaced, AuditRecorded; Derived claims OrderPlaced`

*The code says this slice emits `OrderPlaced`; production says it appends `OrderPlaced` **and** `AuditRecorded`.* That is arguably the most valuable thing a four-source model can tell you, and a first-wins merge destroyed exactly that signal — the losing claim vanished with no trace.

Alongside `Text`, a disagreement carries the structured form for a reader that wants to act on it:

```cs
hotspot.Role;          // EventModelRole.EmittedEvents
hotspot.WinningClaim;  // (Observed, "OrderPlaced, AuditRecorded") — what the merge kept
hotspot.LosingClaim;   // (Derived,  "OrderPlaced")                — what it dropped
```

A pair rather than a list because merges are pairwise: three sources disagreeing about one role leave two findings, each naming the two claims that actually met.

**Nothing is recorded when nothing is lost.** Two sources on the same rung whose lists union have not disagreed about anything; neither have two sources making the *same* claim from different rungs — the code saying `OrderPlaced` and production agreeing is the happy case, and it is silent. A role only one source claims is not a disagreement either, because the other never spoke. What does get recorded is any claim the merge actually dropped, including the one first-wins has always discarded when two same-rung sources name different handlers.

::: tip Hotspots are never arbitrated
Every other role goes to the highest rung that claims it. `Hotspots` always unions, because hotspots are annotations rather than claims about the system — letting a higher-rung source's list replace a lower one would throw away the findings this exists to record.
:::

## Prefer the pending spec

::: tip
Prose is the escape valve, not the default.
:::

The two forms have very different lifecycles:

- A **pending-specification** hotspot is evidence. It appears because a real spec is failing or unbound, and it disappears the moment that stops being true. It cannot lie to you.
- A **prose** hotspot is a note. Nothing retires it but you. Six months on, a stale prose hotspot describing a question the team settled long ago is worse than no hotspot at all, because it teaches people to ignore the magenta ones.

So the moment a question becomes sharp enough to name a scenario, promote it: write the pending spec, `LinksToSpecification` it, and delete the prose line. `"Can an incident be closed before the customer acknowledges the resolution?"` becomes `"Close Incident/Rejects an incident with no acknowledged resolution"`, and from then on the model tracks it for you.

## Reading them back

Both kinds arrive on the assembled descriptor, tagged with their origin:

<!-- snippet: sample_assembling_the_event_model -->
<a id='snippet-sample_assembling_the_event_model'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L43-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_assembling_the_event_model' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`HotspotOrigin` tells them apart; `SpecificationIdentity` is populated only for the pending-spec form, and `Role` / `WinningClaim` / `LosingClaim` only for a source disagreement. Hotspots from different sources are unioned and deduplicated on origin plus text — so prose and a pending spec that happen to share a string stay two distinct hotspots, because they mean two different things.
