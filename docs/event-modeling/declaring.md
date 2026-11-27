# Declaring the Model in Code

Let's say you've just come out of an Event Modeling session for a brand new feature. You've got the slices on the board, everybody agrees on the commands and the events, and now you'd like to start building -- ideally with that model living in your codebase instead of in a photo of a whiteboard that's already drifting away from reality.

You can do exactly that. Write some stub types, declare the model against them with the fluent interface in JasperFx.Events, and write your [Bobcat](https://github.com/JasperFx/bobcat) specifications against those very same stubs. The specs will be red until the handlers exist, which is the whole point. As you build the real code, the roles Wolverine *derives* from your handlers take over from the ones you *declared*, and anywhere the two disagree shows up as a hotspot on the model. That gap between the model and the code is your to-do list.

For the rest of this page, we're going to design a feature IncidentService doesn't have yet: escalating an incident to the on-call agent.

## Start with Stub Types

Stubs first. They don't need any members yet, and that's perfectly fine:

<!-- snippet: sample_escalation_stub_types -->
<a id='snippet-sample_escalation_stub_types'></a>
```cs
// Stubs first. They carry nothing yet, and that's fine -- they're real types the model, the specs
// and eventually the handlers can all refer to.
public record EscalateIncident;
public record IncidentEscalated;
public record SendEscalationDigest;
public record EscalationQueue(Guid Id);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L9-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_escalation_stub_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

These are real types, so the model, the specifications and later on the handlers all refer to the same thing. Add fields as the design firms up. The one exception is a read model you want to assert on in a specification, which needs an `Id` from day one so it can actually be loaded.

## Declaring Slices

Derive from `EventModelDefinition` and open a slice per behavior. The easiest way is with one of the pattern verbs:

<!-- snippet: sample_declaring_the_escalation_feature -->
<a id='snippet-sample_declaring_the_escalation_feature'></a>
```cs
public class EscalationModel : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.InDomain("Incidents").InChapter("Escalation");

        // LogIncident already exists; say that it starts the Incident stream
        model.Command<LogIncident>()
            .TriggeredBy("Customer submits the incident form", TriggerKind.Http)
            .StartsStream<Incident>()
            .Emits<IncidentLogged>();

        model.Command<EscalateIncident>()
            .TriggeredBy("Agent clicks Escalate", TriggerKind.Http)
            .Against<Incident>()
            .Emits<IncidentEscalated>();

        // The system reacting to an escalation. Nobody has written a PageOnCallAgent
        // type yet, so name it for now and stub it later
        model.Automation("PageOnCallAgent")
            .On<IncidentEscalated>()
            .Publishes("PageOnCallAgent");

        // A command fired by Wolverine's scheduled (cron) messages
        model.Automation<SendEscalationDigest>()
            .Reads<EscalationQueue>();

        model.View<EscalationQueue>()
            .From<IncidentEscalated>()
            .From<IncidentClosed>();
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L20-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_the_escalation_feature' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The verbs are:

| Verb | Opens | Named for |
|------|-------|-----------|
| `Command<TCommand>()` / `Command(name)` | A `Command` slice | The command |
| `Automation(name)` | An `Automation` slice -- say what it reacts to with `On<T>()` | Whatever you call it |
| `Automation<TCommand>()` | An `Automation` slice for a command fired by Wolverine's scheduled messages | The command |
| `View<TView>()` / `View(name)` | A `View` slice producing the read model | The read model |
| `Query<TReadModel>(name)` / `Query(name)` | A `Query` slice reading the read model, aggregate or document -- see [Queries](#queries) | Whatever you call it |
| `Query<TReadModel>()` | A `Query` slice reading `TReadModel` | `Get{TReadModel}` |
| `Translation(name)` | A `Translation` slice | Whatever you call it |
| `Slice(name)` | A slice with no pattern yet | Whatever you call it |

::: tip
The type-first verbs name the slice exactly the way Wolverine names the slice it derives from your handler or HTTP endpoint, so the two line up on their own. If you open slices with `Slice(name)`, use the command's short name for the same reason.
:::

`InDomain()` and `InChapter()` on the builder are running defaults for every slice opened after them, and each slice can override either one.

## Declaring Roles

Every role takes either a type or a name:

| Method | Fills |
|--------|-------|
| `Pattern(SlicePattern)` | The slice pattern |
| `TriggeredBy(label)` / `TriggeredBy(label, kind)` / `TriggeredBy(kind)` / `TriggeredBy<T>(kind)` | The trigger's label, kind and type. Declaring `TriggerKind.Human` is fine for a slice the code receives over HTTP, gRPC or the bus: see [who and how](/event-modeling/descriptors#who-triggers-a-slice-and-how-the-code-receives-it) |
| `Command<T>()` | The command or inbound message |
| `HandledBy<T>()` | The handler or endpoint type |
| `Against<T>()` (or `UsesAggregate<T>()`) | An aggregate the slice decides against |
| `StartsStream<T>()` | The aggregate whose stream the slice *starts* -- see below |
| `NoAggregate()` | Deliberately no aggregate -- see [A default aggregate](#a-default-aggregate) |
| `DeciderModel<T>()` | The DCB decider model the slice decides through, rather than single-stream aggregates |
| `Emits<T>()` | An event the slice writes |
| `OnStream<T>()` | The stream the event from the `Emits<T>()` just before goes to -- see [below](#which-stream-an-event-goes-to) |
| `Publishes<T>()` | A non-event message the slice sends out |
| `On<T>()` (or `From<T>()`) | An event the slice reacts to or folds |
| `Reads<T>()` | A read model the slice reads before deciding, or what a `Query` slice reads |
| `Produces<T>()` | A read model the slice produces |
| `Projects<T>()` | A projection that consumes the slice's events |
| `ExternalSystem(name, direction)` | A system outside on one end of the slice |
| `TaggedBy<TTag>()` | A consistency tag the event from the `Emits<T>()` just before carries -- see [Consistency boundaries](#consistency-boundaries) |
| `Decides<TModel>()` / `ByTag<TTag>(...)` | The DCB decider model the slice decides through, read by tag |
| `Reads<T>()` + `By<TTag>(...)` | A read model consulted *by tag*, inside the consistency boundary |
| `UntaggedProjection<T>()` | A projection the boundary deliberately leaves out |
| `Refuses(reason)` | A way the command can refuse |
| `Actor(name, roles...)` / `RequiringRole(role)` | Who triggers the slice -- see [Actors, screens, schedules and external events](#actors-screens-schedules-and-external-events) |
| `FromScreen(name)` | The screen a human trigger acts through, by name |
| `Every(TimeSpan)` / `OnCron(expr)` / `Scheduled(name)` | The cadence of a scheduled automation |
| `External(system)` | The event from the `On<T>()` / `From<T>()` just before comes from an external system |
| `Describe(text)` / `Describe<T>(text)` | Prose about the slice, or about one of its elements |

Calling any of these twice with the same type is harmless.

## Queries

A read-only endpoint is a `Query` slice, not a `View`. The view is the projection; the query is the code that reads it:

<!-- snippet: sample_declaring_query_slices -->
<a id='snippet-sample_declaring_query_slices'></a>
```cs
public class HelpdeskQueries : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        // The projection: events -> read model
        model.View<EscalationQueue>()
            .From<IncidentEscalated>();

        // The endpoint that reads it, as its own slice. Named the way Wolverine.HTTP names
        // a GET endpoint's slice, so the declaration and the code merge
        model.Query<EscalationQueue>("GET /api/incidents/escalated")
            .TriggeredBy("Agent opens the escalation queue", TriggerKind.Http);

        // A query can read an aggregate or a document as well as a projected view
        model.Query<Incident>("GET /api/incidents/{id}")
            .TriggeredBy("Agent opens the incident detail screen", TriggerKind.Http);
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L147-L170' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_query_slices' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Query<T>()` records `T` in the slice's `ReadsFrom`, and that's what links the query to the `View` slice producing `T`. A query can also read an aggregate or a document that no view produces, in which case it simply has no link.

Slices merge by name, and Wolverine names a GET endpoint's slice for its request type when it has one, otherwise for the verb and route (`GET /api/incidents/{id}`). Name your declared query the same way so it lines up with the code. The parameterless `Query<T>()` names the slice `Get{T}`, which matches a Wolverine query message called `GetIncident` and deliberately differs from the `View<T>()` slice for the same type.

A `Query` takes no [default aggregate](#a-default-aggregate). Declaring `Emits<T>()`, `StartsStream<T>()` or `Publishes<T>()` on one is legal, but the model flags it as a [hotspot](/event-modeling/hotspots#a-query-that-writes-is-a-hotspot): a query changes no state.

## Types or Names

Sometimes you aren't ready to even stub a type, and that's okay. Every role also takes a plain name:

<!-- snippet: sample_declaring_roles_by_name -->
<a id='snippet-sample_declaring_roles_by_name'></a>
```cs
public class NotStubbedYet : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Command("ReassignIncident")
            .Against("Incident")
            .Emits("IncidentReassigned")
            .Hotspot("Does a reassignment reset the response SLA?");
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L59-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_roles_by_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A name is recorded as a *declaration* -- no namespace and no assembly -- and once the real type exists, the model matches the two by name. So nothing breaks when `IncidentReassigned` finally shows up as a record in your codebase.

::: warning Prefer stubs
A typo in a name is just a second, mysterious box on the diagram. A typo in a type name is a compile error. Switch to a stub type as soon as you have one.
:::

## Starting a Stream

`StartsStream<T>()` says this slice *starts* a new stream for the aggregate rather than appending to an existing one. That's a different design decision than `Against<T>()`, and the code is different too: there's no existing stream to load, and you have to decide what the new stream's identity will be. Wolverine's scaffolding reads this role to write a `StartStream` instead of a write-model handler.

The aggregate is also recorded as one of the slice's aggregates, so any viewer that doesn't know about this role still draws it.

## Which Stream an Event Goes To

On a board, every event sits in a stream lane. When a slice touches more than one aggregate, say which stream each event goes to with `OnStream<T>()` right after the `Emits<T>()`:

<!-- snippet: sample_emitted_event_stream_lanes -->
<a id='snippet-sample_emitted_event_stream_lanes'></a>
```cs
public class LeadConversion : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        // Decides against the Lead, starts the Account stream, and
        // adds a Contact to an existing stream
        model.Command<ConvertLeadToAccount>()
            .Against<Lead>()
            .StartsStream<Account>()
            .Against<Contact>()
            .Emits<LeadConvertedToAccount>().OnStream<Lead>()
            .Emits<AccountCreated>().OnStream<Account>()
            .Emits<ContactCreated>().OnStream<Contact>();
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/EventTests/EventModeling/EmittedEventStreamTests.cs#L17-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_emitted_event_stream_lanes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The lane is optional for each event. Leave it off and tooling like Wolverine's scaffolding falls back to working out the stream from the aggregates' `Apply` and `Create` methods. The aggregate has to be one the slice decides against or starts, or building the model throws. Don't confuse `OnStream<T>()` with `On<T>()`, which is an event the slice *reacts* to.

The lanes are on the slice descriptor as `EmittedEventStreams`, and `StreamFor(event)` reads one.

## Consistency Boundaries

A Dynamic Consistency Boundary (DCB) command does not decide against one stream. It reads a model folded over the events that carry a *tag* -- a strong-typed id such as `AgentId` -- and appends events carrying the same tag. The model can now say all of that:

<!-- snippet: sample_dcb_stub_types -->
<a id='snippet-sample_dcb_stub_types'></a>
```cs
// A strong-typed id is a consistency tag: events carry it, and a boundary query selects by it
public record AgentId(Guid Value);

public record AssignIncident(IncidentId IncidentId, AgentId AgentId);
public record IncidentAssigned(IncidentId IncidentId, AgentId AgentId);

// The decider model: how many incidents an agent holds, folded over the events tagged with their id
public class AgentWorkload;

// A read model consulted before deciding, and one the boundary deliberately leaves out
public class AgentRoster;
public class AssignmentReport;
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/RoundTwoSamples.cs#L10-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_dcb_stub_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_declaring_consistency_boundaries -->
<a id='snippet-sample_declaring_consistency_boundaries'></a>
```cs
public class IncidentAssignment : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        // The tag catalogue. AgentId is the tag "agentId", a Guid inside; IncidentId is "incidentId"
        model.Tag<AgentId, Guid>();
        model.Tag<IncidentId>();

        model.Command<AssignIncident>()
            // What the command reads, and by which tag. The boundary is derived from these
            .Decides<AgentWorkload>().ByTag<AssignIncident, AgentId>(x => x.AgentId)
            .Reads<AgentRoster>().By<AssignIncident, AgentId>(x => x.AgentId)
            // What it appends, and the tags the event carries
            .Emits<IncidentAssigned>().TaggedBy<AgentId>().TaggedBy<IncidentId>()
            // A projection fed by the event that no decision depends on
            .UntaggedProjection<AssignmentReport>()
            // The ways it can refuse
            .Refuses("Agent is at capacity")
            .Refuses("Incident is already assigned");
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/RoundTwoSamples.cs#L27-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_consistency_boundaries' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Three rules, and the first is the one borrowed from [dcb.events](https://dcb.events):

- **Reads are declared, the boundary is derived.** A command never declares its own consistency boundary. You say what it reads (`Decides<TModel>().ByTag<TTag>(...)` for the decider model, `Reads<TProjection>().By<TTag>(...)` for a projection), and `BoundaryQuery` on the slice descriptor is computed from those reads: one term per tag, with the emitted events carrying that tag. `EventModelDescriptor.BoundaryQueryFor(slice)` adds the events the read models fold, from the aggregates' applied events and the View slices producing them.
- **Only consistency tags enter the model.** `Tag<T>()` declares one in the model-level catalogue (`EventModelDescriptor.Tags`), named in camelCase after its type (`AgentId` is `agentId`); `Tag<TTag, TInner>()` says what primitive is inside, and `Tag("agentId", typeof(Guid))` does the same before the type exists. A tag an event is `TaggedBy` joins the catalogue on its own. Informational labels never go on the model.
- **Refusals are vocabulary.** `Refuses("Agent is at capacity")` records a way the command can refuse on `RejectionReasons`. Bobcat's generated specs fill the same list from the specifications, and a spec refusing for a reason the model does not declare is a [hotspot](/event-modeling/hotspots#an-undeclared-refusal-is-a-hotspot). Code never fills it: that would mean reading method bodies.

The typed `By<TCommand, TTag>(x => x.AgentId)` says which command member supplies the tag value, so a rename keeps the model in step; `By<TTag>("AgentId")` and `By("agentId", "AgentId")` are the string forms, and `By<TTag>()` alone binds by convention. `UntaggedProjection<T>()` is the one that says "this projection is fed by the event but no decision depends on it", which is how a canvas tells a projection the boundary deliberately leaves out from one nobody has thought about.

Once the code exists, Wolverine's derived model fills tags and reads from the store's tag registry and the decider's tag rules; a tag the declaration puts on an event that the code does not is a disagreement on `EventTags`, rendered like an [aggregate identity](#identity-types) disagreement, naming the event.

<!-- JEREMY: whether "the decider's tag rules and Load() calls" is the right description of what wolverine#6005 will read -->

## Actors, Screens, Schedules and External Events

Five things every board has that the descriptor could not say:

<!-- snippet: sample_structural_stub_types -->
<a id='snippet-sample_structural_stub_types'></a>
```cs
public record PaymentCaptured;
public record PaymentRecorded;
public record SendShiftDigest;
public class ShiftRoster;
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/RoundTwoSamples.cs#L55-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_structural_stub_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_declaring_structural_elements -->
<a id='snippet-sample_declaring_structural_elements'></a>
```cs
public class HelpdeskStructure : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Command<AssignIncident>()
            // Who triggers the slice, and what they act through
            .TriggeredBy("Coordinator assigns the incident", TriggerKind.Human)
            .Actor("Coordinator", "dispatch")
            .FromScreen("Assignment board")
            // Prose, for the slice and for one of its elements
            .Describe("A coordinator hands an open incident to an agent with room for it.")
            .Describe<IncidentAssigned>("The incident now counts against the agent's capacity.");

        // An event from outside the model, translated in
        model.Translation("RecordPayment")
            .From<PaymentCaptured>().External("Payments")
            .Emits<PaymentRecorded>();

        // A scheduled automation, with its cadence
        model.Automation<SendShiftDigest>()
            .Every(TimeSpan.FromHours(8))
            .Reads<ShiftRoster>();
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/RoundTwoSamples.cs#L64-L93' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_structural_elements' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

- **Actor.** `Actor("Coordinator", "dispatch")` names who triggers the slice and the roles the slice requires of them; `RequiringRole(role)` after a `TriggeredBy("Coordinator", TriggerKind.Human)` makes the label the actor. It lands on `Actor` as a name plus `RequiredRoles`. Wolverine fills the required roles from authorization metadata where it can, and a difference between what you declared and what the endpoint requires is a real finding, reported as a disagreement on `Actor`.
- **Screen.** `FromScreen("Assignment board")` is the screen's name and nothing else -- no content, no images, no HTML. It renders as a `Screen` element in the wireframe lane, pointing at the trigger.
- **Schedule.** `Every(TimeSpan)`, `OnCron("0 9 * * MON")` or `Scheduled("month-end")` put the cadence of a scheduled automation on `Schedule`, beside `TriggerKind.JobScheduler`, which they stamp if nothing else has claimed the trigger kind. The derived model fills it from the registered recurring schedules.
- **External events.** `From<PaymentCaptured>().External("Payments")` flags the consumed event as coming from outside the model and ties it to the slice's external system, adding an inbound `ExternalSystem` if there is none yet. The canvas draws the event on a stream row named for the system rather than an aggregate. The derived model sets the flag when the consumed type arrives on a non-local endpoint and no slice in the model emits it.
- **Descriptions.** `Describe("…")` is prose about the slice; `Describe<T>("…")` is prose about one of its elements, by type rather than by position in the chain, so the order of the calls never changes what a description is about. Plain text or markdown, shown on hover, carried into stubs and specs as comments. On a merge the richer text wins and nothing is ever recorded: two descriptions are two drafts of one paragraph, not two claims about the system.

## Fields

The descriptor used to carry types as names and know nothing about what was inside them. A *shape* is the fields of one modelled type -- flat fields plus one level of nesting by reference, where a field whose type is itself a modelled type is a reference to that element. No schema language.

Once the code exists, the fields come from it. The `JasperFx.SourceGenerator` reads the public properties of every type an `EventModelDefinition` names through a type-first verb and writes their shapes into the same manifest `AddDiscoveredEventModels` already reads, so the console path never reflects over a record. The flags reflection cannot see come from attributes:

<!-- snippet: sample_field_attributes -->
<a id='snippet-sample_field_attributes'></a>
```cs
public record IncidentEscalatedToAgent(
    // The system mints it, so no command has to supply it
    [property: Generated] Guid EscalationId,
    IncidentId IncidentId,
    // Personally identifiable; a canvas and an export mark it
    [property: Pii] string AgentEmail,
    DateTimeOffset? Deadline);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/RoundTwoSamples.cs#L95-L105' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_field_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

| Flag | Set by |
|------|--------|
| `Identity` | `[Identity]` (JasperFx), `[Key]` (data annotations), or a member named `Id` |
| `Generated` | `[Generated]` (JasperFx) or `[DatabaseGenerated]` |
| `Pii` | `[Pii]` (JasperFx) |
| `Optional` | A nullable type, unless `[Required]` or the `required` modifier says otherwise |
| `Cardinality` | `Many` for an array or anything enumerable, with the element type as the field's type |
| `Description` | The member's `<summary>`, or the record's `<param>` for a positional parameter |

Nothing is inferred from a method body. Tags on fields come from the store's registry, on Wolverine's side.

For stub-first work -- a board import, a feature designed before the stubs have members -- declare the shapes:

<!-- snippet: sample_declaring_field_shapes -->
<a id='snippet-sample_declaring_field_shapes'></a>
```cs
public class HelpdeskShapes : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Command<AssignIncident>()
            .Against<Incident>()
            .Emits<IncidentAssigned>();

        model.View<AgentRoster>().From<IncidentAssigned>();

        // The fields the stubs will have, declared before they do. Once the types exist, the
        // source generator reads the real fields and these become the to-do list
        model.Shape<AssignIncident>()
            .Field("IncidentId").OfType<IncidentId>().Identity()
            .Field("AgentId").OfType<AgentId>().Tagged<AgentId>();

        model.Shape<IncidentAssigned>()
            .Field("IncidentId").OfType<IncidentId>().Identity()
            .Field("AgentId").OfType<AgentId>().Tagged<AgentId>()
            .Field("AssignedAt").OfType<DateTimeOffset>().Generated();

        model.Shape<AgentRoster>()
            .Field("Id").OfType<AgentId>().Identity()
            // Named differently from the event's field, so say where it comes from
            .Field("Incidents").OfType<IncidentId>().Many().From<IncidentAssigned>(x => x.IncidentId)
            .Field("Notes").Optional().Describe("Free text the coordinator keeps per agent");
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/RoundTwoSamples.cs#L107-L140' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_field_shapes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Shape<T>()` opens a shape on the model; each `Field(name)` takes a type (`OfType<T>()`, default `string`), a cardinality (`Many()`), and the flags (`Optional()`, `Identity()`, `Tagged<TTag>()`, `Generated()`, `Pii()`). `From<TSource>(x => x.Member)` is the board's *mapping*: where the value comes from when the names differ, which is what the information-completeness check follows. Example values stay on the scenario, never on the field.

Two rules decide what happens when the code shows up:

- **A declared field the code lacks is a disagreement; a derived field the declaration lacks is not.** The merge keeps the code's fields per type and records every declared field with no counterpart as a model-level `SourceDisagreement` on `Fields`. Code may carry more than the board knew, so the other direction is silent. A declared field's mapping, tag and description travel onto the derived field, as a stream lane travels onto a derived event.
- **A field from nowhere is a question.** Once the slices and shapes are merged, the model asks where each view field and each emitted-event field comes from -- see [A field from nowhere is a question](/event-modeling/hotspots#a-field-from-nowhere-is-a-question). Only for a slice with a declared or specified rung: a purely derived model gets none.

<!-- JEREMY: the "once the type exists" reading -- an empty stub record IS a type with no fields, so every declared field on it is a disagreement until bobcat#851 writes typed stubs. Is that the to-do list you want, or should an empty derived shape stay silent? -->

## A Default Aggregate

Most commands in a chapter decide against the same aggregate. Say so once with `ForAggregate<T>()`, the companion to `InChapter()`:

```csharp
public override void Configure(EventModelBuilder model)
{
    model.InChapter("BookingAppointments");
    model.ForAggregate<Appointment>();          // or ForAggregate("Appointment")

    model.Command<ConfirmAppointment>()         // decides against Appointment
        .Emits<AppointmentConfirmed>();

    model.Command<RescheduleAppointment>()
        .Against<Calendar>()                    // an explicit Against replaces the default
        .Emits<AppointmentRescheduled>();

    model.Command<ProposeAppointment>()
        .StartsStream<Appointment>()            // a slice that starts a stream ignores the default
        .Emits<AppointmentProposed>();

    model.Command<RecordWalkIn>()
        .NoAggregate()                          // deliberately none
        .Emits<WalkInRecorded>();

    model.Command<BookSlot>()
        .DeciderModel<SlotBooking>()            // a DCB decider model instead
        .Emits<SlotBooked>();
}
```

The rules:

- The last `ForAggregate` wins, and applies to every **command** slice opened after it that declares none of `Against`, `StartsStream`, `NoAggregate` or `DeciderModel`. Earlier slices, views, automations and translations are untouched.
- Like `InDomain` and `InChapter`, it never carries into another definition: each definition gets a fresh builder.
- `ForAggregate` also declares the aggregate on the model. `Aggregate<T>()` is unchanged: it declares an aggregate and sets no default.
- `NoAggregate()` is for a slice that genuinely decides against nothing. Tooling such as Wolverine's scaffold stops warning about the missing aggregate. Combining it with `Against` on one slice throws.
- `DeciderModel<T>()` only records the decider type for now; how its events are selected waits on the Dynamic Consistency Boundary design.

Each slice says *why* it has its aggregate on `AggregateDeclaration` -- `Default`, `Explicit`, `None` or `DeciderModel` -- so tooling can explain it. A default-applied aggregate is still a declared claim: if the handler decides against something else, the code wins and the difference becomes a hotspot.

## Aggregates

You can declare the aggregates in the model itself. The events each aggregate applies get filled in from the code later:

<!-- snippet: sample_declaring_aggregates -->
<a id='snippet-sample_declaring_aggregates'></a>
```cs
public class HelpdeskAggregates : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Aggregate<Incident>();
        model.Aggregate("Customer", AggregateKind.ReadAggregate);
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L76-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_aggregates' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Identity types

Say what identifies an aggregate's stream with `IdentifiedBy<T>()`, right after declaring the aggregate with `Aggregate<T>()` or `ForAggregate<T>()`:

<!-- snippet: sample_declaring_an_aggregate_identity -->
<a id='snippet-sample_declaring_an_aggregate_identity'></a>
```cs
public record IncidentId(Guid Value);

public class HelpdeskIdentities : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        // The aggregate's stream is identified by a strong-typed id
        model.Aggregate<Incident>().IdentifiedBy<IncidentId>();

        // Before the types exist, by name
        model.Aggregate("Customer").IdentifiedBy("CustomerNumber");
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L127-L145' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_an_aggregate_identity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That lands on `AggregateDescriptor.IdentityType`. Wolverine's scaffold reads it to write strong-typed ids, and Bobcat's generated specifications use it to mint one (`new IncidentId(Guid.NewGuid())`).

You don't need it once the code exists. The store reads the identity type off the aggregate's single-stream projection (the `TId` of `SingleStreamProjection<TDoc, TId>`), and that derived claim wins. If it differs from what you declared, the model carries a `SourceDisagreement` hotspot on the `AggregateIdentity` role. `IdentifiedBy` with no aggregate declared before it throws.

## Modules are Domains

In a modular monolith, a module is a `Domain`. Membership is **declared, never inferred** -- nothing in the Critter Stack is going to guess a module from your namespace or assembly names on its own. You declare it with policies in the model:

<!-- snippet: sample_declaring_modules_as_domains -->
<a id='snippet-sample_declaring_modules_as_domains'></a>
```cs
public class HelpdeskModules : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        // Everything in a namespace, and the namespaces beneath it...
        model.Domain("Incidents").IncludesNamespace("IncidentService.Incidents");

        // ...or the namespace of a marker type...
        model.Domain("Billing").IncludesNamespaceOf<BillingModule>();

        // ...or one specific handler
        model.Domain("Retention").Includes<IncidentRetentionHandler>();
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L91-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_modules_as_domains' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_declaring_modules_as_domains-1'></a>
```cs
public class ModularMonolithModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        // Everything in the Billing module's namespace is Billing...
        model.Domain("Billing").IncludesNamespace("EventTests.EventModeling.Modules.Billing");

        // ...and Shipping is declared by a marker type's namespace
        model.Domain("Shipping").IncludesNamespaceOf<ShippingModule>();
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/EventTests/EventModeling/DomainDeclarationTests.cs#L17-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declaring_modules_as_domains-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

or right on the handler or HTTP endpoint, either on the class or on a single method:

<!-- snippet: sample_domain_attribute_on_a_handler -->
<a id='snippet-sample_domain_attribute_on_a_handler'></a>
```cs
[Domain("Retention")]
public class IncidentRetentionHandler
{
    public void Handle(ArchiveIncident command)
    {
        // archive the incident
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L114-L125' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_domain_attribute_on_a_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine uses these when it derives slices from your handlers, so that two modules handling the same message come out as two slices in two different domains.

The most specific declaration wins:

1. `[Domain]` on the handler method
2. `[Domain]` on the handler or endpoint class
3. A policy naming the type with `Includes<T>()`
4. The *deepest* matching namespace policy -- `MyApp.Billing` covers `MyApp.Billing.Invoices`, but not `MyApp.BillingReports`
5. An assembly policy

`EventModelDomains.Resolve()` applies these rules, and it does two things you should know about. First, if two declarations at the same level name different domains, it won't pick one -- it reports the result as ambiguous. Second, every other declaration that disagreed with the answer comes back in `Conflicts`, even when it was legitimately overridden. A namespace policy that says `Billing` overridden by an attribute that says `Shipping` might be exactly what you meant, or it might be a mistake, and only you know which.

## When the Code Shows Up

Everything you declare sits on the bottom rung of the [provenance ladder](/event-modeling/descriptors#provenance-decides-the-merge). Once Wolverine derives a role from your code, the derived claim wins. If the two agree, nothing happens at all. If they don't, the merge keeps the code's answer and records yours as a [source disagreement](/event-modeling/hotspots#a-source-disagreement-is-a-hotspot), naming the model that declared it.

That's the workflow in a nutshell:

1. Stub the types
2. Declare the model
3. Write the specifications -- they'll be red
4. Build the handlers until the specifications pass and the disagreements go away

Some things never come from code. Slice names, trigger labels like "Agent clicks Escalate", domains and chapters only ever come from declarations, so those stay yours. Specification links can come from the specifications themselves -- see [Links from the specifications](/event-modeling/descriptors#links-from-the-specifications).

::: tip
For a while, this builder could only name, group and annotate slices -- the [overlay](/event-modeling/overlay). The provenance ladder is what made it safe to declare roles again: a declaration can no longer overwrite what the code actually does.
:::

## Registering the Model

Register each definition with the container, the same way as an overlay -- or all of them at once with `AddDiscoveredEventModels(assembly)`. A definition that doesn't override `Name` contributes to the application's model, so one definition per chapter merges with the code without any further wiring:

<!-- snippet: sample_registering_declared_models -->
<a id='snippet-sample_registering_declared_models'></a>
```cs
services.AddEventModel<EscalationModel>();
services.AddEventModel<HelpdeskModules>();
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L176-L181' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_declared_models' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
