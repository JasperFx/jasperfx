# Descriptors and the Wire

Every source of an Event Model — Wolverine's chains, the Bobcat generator, a source generator, your overlay — writes into the same two records, and every viewer reads from them. This page is the shape.

## `EventModelSliceDescriptor`

One slice. The positional constructor is the original 2.x shape and is kept source- and binary-compatible; everything added since is an `init` property with a safe default, so older payloads and precompiled callers keep working.

| Slot | Filled by | Holds |
|------|-----------|-------|
| `Name` | Both | Display name; also the merge key across sources |
| `Pattern` | Derived | `Command`, `View`, `Automation` or `Translation` |
| `TriggerKind` | Derived | `Http`, `Grpc`, `MessageHandler`, `JobScheduler`, `Human`, `External` |
| `TriggerOrigin` | Derived | HTTP verb + route, gRPC service + method, or a label |
| `TriggerType` | Derived | CLR type of the trigger, e.g. an inbound request DTO |
| `TriggerLabel` | **Overlay** | "Agent clicks Close" |
| `CommandType` | Derived | The inbound message type |
| `HandlerType` | Derived | The handler or endpoint type — distinct from the aggregates |
| `AggregateTypes` | Derived | Projected write models the handler decides against |
| `EmittedEvents` | Derived | Events the slice writes, in declaration order |
| `PublishedMessages` | Derived | Non-event messages — cascaded commands, integration messages |
| `ProjectionTypes` | Derived | Projections consuming the slice's events |
| `ReadModelTypes` | Derived | Read models the slice produces |
| `ConsumedEvents` | Derived | Events the slice's projection / read model / automation **applies** |
| `ReadsFrom` | Derived | Read models the slice **reads before deciding** |
| `ExternalSystems` | Derived | Systems on either end of a translation |
| `Specifications` | Derived (mostly) | Bound specs by `{Feature}/{Scenario}` plus resolved types |
| `Hotspots` | Both | Pending specs (derived) and prose (overlay) |
| `Domain` | **Overlay** | Bounded context |
| `Chapter` | **Overlay** | A named span of slices — the navigation unit above `Domain` |

### What a slice reads

The first three roles above record what a slice *produces*. `ConsumedEvents` and `ReadsFrom` are the other direction, and without them two ordinary arrows could not be drawn at all:

- **State View** — event → read model, across slices. Nothing could say "this projection applies `AccountOpened`", so a View slice existed on a canvas only when somebody declared one.
- **Automation input** — Event → Read Model (todo list) → ⚙ Command. `ReadModelTypes` meant "reads from *or* produces", so nothing linked a read model *to* a processor.

Each `ConsumedEvents` entry becomes an `Event` element in the consuming slice's own event-stream lane — the "repeat the sticky where it is consumed" convention — and each `ReadsFrom` entry a `ReadModel` element pointing at the processor. An event a slice both emits and consumes is **one** element, and the same for a read model it both reads and produces.

`Chapter` merges like `Domain`: first non-null wins, and a genuine disagreement between two sources becomes a `SourceDisagreement` hotspot rather than a silent drop.

This is what a derived source produces for `CloseIncident`:

<!-- snippet: sample_a_derived_slice -->
<a id='snippet-sample_a_derived_slice'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L72-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_a_derived_slice' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## `EventModelDescriptor`

The whole model: its `Slices`, the `Aggregates` those slices reference by type (each with its kind and applied events), model-level `Hotspots`, and the cross-slice `Links`.

## The rendering contract

`Elements`, `Edges` and `Links` are **computed from the typed roles on every read**. They are not stored and cannot disagree with the roles underneath them; a deserializer simply ignores whatever arrived and recomputes.

Each element carries a deterministic id — `{slice}/{kind}/{type full name or label}` — a kind, a lane, a label, and the CLR type identity when it has one. Edges reference elements by that id.

### `Links` — the relationships *between* slices

`Elements` and `Edges` are slice-local: element ids are scoped to a slice, so the same event type in two slices is two ids, and neither says how the slices relate. `EventModelDescriptor.Links` is that missing axis — one entry per cause→effect relationship, with both ends given as element ids so a viewer that has laid out the elements can draw a link with no further resolution.

| Kind | Means |
|------|-------|
| `EventTriggers` | A emits `E`; B's `CommandType` or `TriggerType` **is** `E` |
| `MessageTriggers` | A publishes `M`; B handles `M` — the cascading-message arrow |
| `EventConsumed` | A emits `E`; B's `ConsumedEvents` contains `E` — the State View arrow |
| `ReadModelRead` | A produces `R`; B's `ReadsFrom` contains `R` — the Automation input |

Three rules are worth knowing:

- **A slice never links to itself.** A slice that emits an event and handles it already says so through its own `Edges`.
- **Type identity follows the merge rule.** Key on `FullName` when *both* sides carry a non-empty `AssemblyName`, else on the short `Name` — an empty assembly name marks a declaration whose `FullName` is a synthesized guess, so a spec-declared slice still links to the code that implements it.
- **Order is deterministic** — by `FromSlice` declaration order, then `ToSlice`, then kind, then type — so a serialized document is byte-stable and a diff of two exports shows only what changed.

The join is a public pure function, `EventModelLinks.Compute(model)`, so a consumer that needs the relationship for something other than rendering (Wolverine's Automation reclassification, an MCP "what consumes `OrderPlaced`?" tool) uses the same rule rather than a second copy of it. Because links are computed over the *merged* slices, a relationship no single source could see — one source knows the event is emitted, another knows the second slice handles it — appears once the model is assembled.

<!-- snippet: sample_reading_the_rendering_contract -->
<a id='snippet-sample_reading_the_rendering_contract'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L123-L138' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_reading_the_rendering_contract' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`EventModelPalette.ColorFor` is the shared reference so two viewers of one descriptor agree on what a colour means:

| Kind | Lane | Colour |
|------|------|--------|
| `Trigger` | Wireframe | `#FFFFFF` white |
| `ExternalSystem` | Wireframe | `#F8BBD0` pink |
| `Hotspot` | Wireframe | `#E91E63` magenta |
| `Command` | Command | `#5B9BD5` blue |
| `Handler` | Command | `#5B9BD5` blue, outlined |
| `Aggregate` | Command | `#FFF2A8` pale yellow |
| `Event` | EventStream | `#F5A623` orange |
| `Message` | EventStream | `#5B9BD5` blue, dashed |
| `Projection` | ReadModel | `#7ED321` green, outlined |
| `ReadModel` | ReadModel | `#7ED321` green |

## Discovery and assembly

`EventModelDiscovery` walks every registered `IEventModelDefinitionSource`, asks each for its descriptor (skipping any that return null), and folds the results into one descriptor per model name:

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

### Provenance decides the merge

Four producers feed one descriptor — Gherkin specs, the C# overlay and code-first specs, Wolverine's chains, and runtime observation from CritterWatch — so something has to arbitrate when two of them describe the same slice. That something is a three-rung ladder of authority, `EventModelProvenance`:

| Rung | Who | Beats |
| --- | --- | --- |
| `Declared` | A Gherkin spec, a code-first spec, the `EventModelDefinition` overlay | — |
| `Derived` | Wolverine's handler / HTTP / gRPC chains, the source generator | `Declared` |
| `Observed` | CritterWatch watching a running system | `Derived`, `Declared` |

Production beats what the code implies, and the code beats what somebody wrote down. A source declares its rung once, on `IEventModelDefinitionSource.Provenance`; `EventModelDiscovery.DiscoverAsync` stamps it onto every slice the source returns.

::: warning This inverts the pre-2.56 ordering
Registration order used to be the mechanism — `WolverineEventModelSource` was registered at index 0 specifically so derived roles would beat overlays. It is now only a **tie-breaker between sources on the same rung**. `Provenance` defaults to `Declared` on every source, so an application whose sources have not been stamped yet gets exactly the merge it got before.
:::

**Precedence is per claimed role, not wholesale.** A role is claimed when the slice carries a value for it — a non-null scalar or a non-empty list — and a source that does not claim a role never overrides one that does, whatever rung it sits on. This is why slice names, domains, trigger labels and specification links keep coming from declarations and keep winning by default: production has no opinion about what a slice is called. The ladder only decides *factual* roles — which events are emitted, which aggregates are touched, which read models are produced.

Concretely:

- **Scalars** go to the highest rung that claims them; a tie keeps the first value.
- **Lists** go to the highest rung that claims them **outright** — a higher rung *replaces* rather than unions, because unioning derived `{A, C}` with observed `{A, B}` invents a slice emitting three events that nobody claimed. Lists claimed at the **same** rung union in order and deduplicate by identity: types by full name, external systems by direction + name, specifications by identity.
- **Hotspots** always union, whatever the rungs. They are annotations rather than claims about the system, and arbitrating them would discard findings.
- **Any dropped claim** becomes a [`SourceDisagreement` hotspot](/event-modeling/hotspots#a-source-disagreement-is-a-hotspot) naming the role, both claims and the rung each came from — so a losing claim is recorded rather than silently discarded.
- **Slices** fold by name; slice order is first appearance.
- **Aggregates** union by type full name.

Ask a merged slice where any role came from with `ProvenanceFor`:

```cs
slice.ProvenanceFor(EventModelRole.EmittedEvents);  // Observed — production claimed these
slice.ProvenanceFor(EventModelRole.Domain);         // Declared — only the overlay claims a domain
slice.ProvenanceFor(EventModelRole.HandlerType);    // null — nothing claims it
```

Every rendered `EventModelElement` carries the same answer on its own `Provenance`, so a viewer can shade "production has seen this happen" differently from "somebody wrote it down" without re-deriving anything.

Merging two slices with different names throws — slices merge by name, and a mismatch means a bug in whoever assembled the list.

## Serialization

The descriptors are plain records and serialize with `System.Text.Json` as-is. CritterWatch's wire shape is camelCase with camelCase string enums:

<!-- snippet: sample_serializing_an_event_model -->
<a id='snippet-sample_serializing_an_event_model'></a>
```cs
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

var json = JsonSerializer.Serialize(model, options);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/EventModelUsageSamples.cs#L143-L153' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_serializing_an_event_model' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Because `Elements`, `Edges` and `Links` are computed properties, they go **out** on the wire — a viewer gets the rendering contract without a second transform — and are ignored coming back in. A payload whose `links` disagree with its roles is recomputed rather than believed.

## Where the slices come from

Four rungs write into this vocabulary, and a slice is usually assembled from more than one:

| Rung | Source | Claims |
|------|--------|--------|
| Declared | Bobcat specs, a curated model file, your overlay | Names, domains, chapters, hotspots, spec links |
| Derived | Wolverine's chains; the event store's projection registry | Commands, handlers, emitted events, projections, consumed events |
| Observed | CritterWatch watching a running system | What actually happened |

The store rung is `ProjectionEventModelSource`, which reads the registered projections straight out of `IEventStore` — one `View` slice per projection, carrying the projection type, the document it produces and every event it applies. It is registered by the store's own `AddMarten` / `AddPolecat` / `AddFisher`, or directly:

```cs
services.AddProjectionEventModelSource();
```

Slices are named after the **document type**, which is how a declared View slice is already named — so the spec-declared `AccountBalance` and the store-derived one merge into one slice carrying both a `Declared` and a `Derived` claim, rather than two stickies that say the same thing.
