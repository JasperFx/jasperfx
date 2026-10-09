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
| `TriggeredBy(label)` / `TriggeredBy(label, kind)` / `TriggeredBy(kind)` / `TriggeredBy<T>(kind)` | The trigger's label, kind and type |
| `Command<T>()` | The command or inbound message |
| `HandledBy<T>()` | The handler or endpoint type |
| `Against<T>()` (or `UsesAggregate<T>()`) | An aggregate the slice decides against |
| `StartsStream<T>()` | The aggregate whose stream the slice *starts* -- see below |
| `NoAggregate()` | Deliberately no aggregate -- see [A default aggregate](#a-default-aggregate) |
| `DeciderModel<T>()` | The DCB decider model the slice decides through, rather than single-stream aggregates |
| `Emits<T>()` | An event the slice writes |
| `Publishes<T>()` | A non-event message the slice sends out |
| `On<T>()` (or `From<T>()`) | An event the slice reacts to or folds |
| `Reads<T>()` | A read model the slice reads before deciding |
| `Produces<T>()` | A read model the slice produces |
| `Projects<T>()` | A projection that consumes the slice's events |
| `ExternalSystem(name, direction)` | A system outside on one end of the slice |

Calling any of these twice with the same type is harmless.

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
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EventModeling/DeclaringTheModelSamples.cs#L131-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_declared_models' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
