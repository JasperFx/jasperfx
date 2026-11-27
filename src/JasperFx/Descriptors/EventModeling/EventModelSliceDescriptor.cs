using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Wire descriptor for a single slice of an Event Model — the one vocabulary every
/// source writes into (Wolverine chains, the Bobcat generator, the CritterWatch
/// source generator via <see cref="HandlerRelationshipDescriptor.ToSliceDescriptor"/>,
/// and the naming / grouping / linking overlay) and every viewer reads from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> The positional constructor is the original 2.x shape and is kept
/// source- and binary-compatible; everything jasperfx#687 added is an <c>init</c>
/// property with a safe default, so precompiled callers and older JSON payloads keep
/// working. <see cref="Elements"/> and <see cref="Edges"/> are <em>computed</em> from the
/// typed roles on every read, so the wire carries the rendering contract without the two
/// ever disagreeing; a deserializer simply ignores them and recomputes.
/// </para>
/// <para>
/// <b>Roles are derived, not declared</b> (jasperfx#687 reshape, 2026-08-20): the command,
/// handler, aggregates, emitted events, published messages, projections, read models,
/// trigger kind and slice pattern are stamped by the source that can see them. The overlay
/// (<c>EventModelDefinition</c>, which stays in JasperFx.Events with the rest of the authoring
/// API) only names, groups, annotates and links. Two slices
/// with the same <see cref="Name"/> from different sources are folded into one by
/// <see cref="Merge"/>.
/// </para>
/// </remarks>
/// <param name="Name">Display name of the slice; also the merge key across sources.</param>
/// <param name="TriggerLabel">Free-form trigger label, when one was supplied.</param>
/// <param name="TriggerType">CLR trigger type, when one was supplied (e.g. an inbound HTTP request DTO).</param>
/// <param name="CommandType">The inbound message type — the command — when one was derived.</param>
/// <param name="HandlerType">
///     The handler / endpoint type that processes the command. Distinct from the aggregate(s). The
///     <em>user's</em> class — never a type the framework generated around it, such as Wolverine's
///     <c>MessageHandler</c> or <c>HttpHandler</c> subclasses. See <see cref="HandlerMethod"/>.
/// </param>
/// <param name="EmittedEvents">Event types emitted by the slice, in declaration order.</param>
/// <param name="ProjectionTypes">Projection types that consume the slice's events.</param>
/// <param name="ReadModelTypes">Read-model types the slice reads from or produces.</param>
public sealed record EventModelSliceDescriptor(
    string Name,
    string? TriggerLabel,
    TypeDescriptor? TriggerType,
    TypeDescriptor? CommandType,
    TypeDescriptor? HandlerType,
    IReadOnlyList<TypeDescriptor> EmittedEvents,
    IReadOnlyList<TypeDescriptor> ProjectionTypes,
    IReadOnlyList<TypeDescriptor> ReadModelTypes)
{
    /// <summary>A slice with nothing but a name — what the overlay starts from.</summary>
    public static EventModelSliceDescriptor Named(string name)
        => new(name, null, null, null, null, Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>());

    // jasperfx#807. A partial slice is the designed-for shape -- Merge folds several sources' halves
    // together by Name, and a source is by definition partial -- but System.Text.Json hands `default`
    // to any constructor parameter the JSON does not carry. So a slice sent without `emittedEvents` /
    // `projectionTypes` / `readModelTypes` arrived with them NULL despite the non-nullable
    // declaration, and buildGraph() blew up on the way back OUT, through the computed Elements
    // getter. The init-only collections below never had the problem: their initializers hold when the
    // member is absent. Redeclaring these three gives them the same treatment, so the non-nullable
    // declaration means what it says however the record was built.

    /// <summary>Event types emitted by the slice, in declaration order. Never null.</summary>
    public IReadOnlyList<TypeDescriptor> EmittedEvents { get; init; } = EmittedEvents ?? Array.Empty<TypeDescriptor>();

    /// <summary>Projection types that consume the slice's events. Never null.</summary>
    public IReadOnlyList<TypeDescriptor> ProjectionTypes { get; init; } = ProjectionTypes ?? Array.Empty<TypeDescriptor>();

    /// <summary>Read-model types the slice reads from or produces. Never null.</summary>
    public IReadOnlyList<TypeDescriptor> ReadModelTypes { get; init; } = ReadModelTypes ?? Array.Empty<TypeDescriptor>();

    /// <summary>
    /// Which of the four canonical Event Modeling patterns this slice is. Null until a source
    /// derives it.
    /// </summary>
    public SlicePattern? Pattern { get; init; }

    /// <summary>
    /// What starts the slice — on a merged slice, <em>how</em> the code receives it. Null until a source
    /// claims it.
    /// </summary>
    /// <remarks>
    /// A declaration's <see cref="EventModeling.TriggerKind.Human"/> stays here until code is merged in.
    /// When the code receives the slice through a kind a person can reach
    /// (<see cref="TriggerKindCompatibility.CanBeHumanTriggered"/>), the merge keeps the derived kind here
    /// and moves the <see cref="EventModeling.TriggerKind.Human"/> to <see cref="TriggeredBy"/>, so
    /// viewers that label a trigger by this member keep labelling it by the route or handler. An HTTP
    /// endpoint folded with a message handler keeps <see cref="EventModeling.TriggerKind.Http"/> here, in
    /// either source order; see <see cref="AdditionalEntryPoints"/>.
    /// </remarks>
    public TriggerKind? TriggerKind { get; init; }

    /// <summary>
    /// <em>Who</em> triggers the slice, when that differs from <em>how</em> the code receives it — in
    /// practice <see cref="EventModeling.TriggerKind.Human"/> on a slice whose
    /// <see cref="TriggerKind"/> is <see cref="EventModeling.TriggerKind.Http"/>,
    /// <see cref="EventModeling.TriggerKind.Grpc"/> or <see cref="EventModeling.TriggerKind.MessageHandler"/>.
    /// Null otherwise.
    /// </summary>
    /// <remarks>
    /// Stamped by <see cref="Merge"/> when a declared <see cref="EventModeling.TriggerKind.Human"/> meets a
    /// compatible derived kind, which is not a disagreement: the two answer different questions. A slice
    /// that has met no code still carries the <see cref="EventModeling.TriggerKind.Human"/> on
    /// <see cref="TriggerKind"/>; <see cref="IsTriggeredByHuman"/> asks the question either way.
    /// </remarks>
    public TriggerKind? TriggeredBy { get; init; }

    /// <summary>
    /// Whether a person triggers this slice: <see cref="TriggerKind"/> or <see cref="TriggeredBy"/> is
    /// <see cref="EventModeling.TriggerKind.Human"/>.
    /// </summary>
    public bool IsTriggeredByHuman()
        => TriggerKind == EventModeling.TriggerKind.Human || TriggeredBy == EventModeling.TriggerKind.Human;

    /// <summary>
    /// The name of the method on <see cref="HandlerType"/> that handles the command — the user's
    /// <c>Handle</c> / <c>Consume</c> / endpoint method, so a viewer can navigate to the code. Null when no
    /// source knows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like <see cref="HandlerType"/>, this names the code the user wrote, never generated code: a
    /// Wolverine source stamps <c>chain.Handlers[0].HandlerType</c> / <c>.Method.Name</c> for a message
    /// handler and <c>HttpChain.Method.HandlerType</c> / <c>.Method.Name</c> for an endpoint, not the
    /// generated <c>MessageHandler</c> / <c>HttpHandler</c> subclass and its <c>Handle</c> override.
    /// </para>
    /// <para>
    /// Detail of <see cref="HandlerType"/> for merging, the way a route name is detail of the trigger
    /// origin: it travels with the winning handler, a known method fills in an unknown one when both sides
    /// name the same handler type, and two different methods on the same type leave a
    /// <see cref="HotspotOrigin.SourceDisagreement"/> on <see cref="EventModelRole.HandlerType"/>, unless
    /// they are an endpoint and a message handler on one class (<see cref="AdditionalEntryPoints"/>).
    /// </para>
    /// </remarks>
    public string? HandlerMethod { get; init; }

    /// <summary>
    /// The ways into this slice beyond the one <see cref="TriggerKind"/> and <see cref="HandlerType"/> name
    /// (jasperfx#1099). Empty on almost every slice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stamped by <see cref="Merge"/>, never by a source. Wolverine.HTTP names an endpoint's slice for its
    /// request type so that the endpoint and the message handler for one command fold into one slice, and
    /// both entry points are real, but <see cref="TriggerKind"/>, <see cref="HandlerType"/> and
    /// <see cref="HandlerMethod"/> hold one value each. They keep the HTTP endpoint, the entry point
    /// <see cref="TriggerOrigin"/> describes, and the message handler lands here.
    /// </para>
    /// <para>
    /// Nothing else on the descriptor could hold it: a hotspot would report the fold as a problem, which is
    /// the noise this exists to remove. Rendered nowhere by the slice's own <see cref="Elements"/>, so a
    /// viewer that knows nothing of this member draws what it always did, one handler per slice. A console
    /// reads it to offer the other handler for navigation, and <see cref="EventModelSpecifications.Link"/>
    /// matches a specification's namespace against it as well as against <see cref="HandlerType"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<EventModelEntryPoint> AdditionalEntryPoints { get; init; } = Array.Empty<EventModelEntryPoint>();

    /// <summary>
    /// Structured detail of the trigger for the kinds that have it — HTTP route + verb, gRPC
    /// service + method, the projection raising a side effect, or a display label. Shares the
    /// <see cref="PublisherOrigin"/> shape with <see cref="HandlerRelationshipDescriptor.Origin"/>
    /// so the two fold into one vocabulary.
    /// </summary>
    public PublisherOrigin? TriggerOrigin { get; init; }

    /// <summary>
    /// The aggregate(s) / projected write model(s) the handler decides against — a list, because one
    /// Critter Stack command handler may load several projected models. Distinct from
    /// <see cref="HandlerType"/>. The aggregate <em>elements</em> themselves (kind, applied events)
    /// live on <see cref="EventModelDescriptor.Aggregates"/>; this is the reference by type.
    /// </summary>
    public IReadOnlyList<TypeDescriptor> AggregateTypes { get; init; } = Array.Empty<TypeDescriptor>();

    /// <summary>
    /// Non-event messages the slice publishes — cascaded commands, integration messages. Kept
    /// apart from <see cref="EmittedEvents"/> so the event stream lane shows only events.
    /// </summary>
    public IReadOnlyList<TypeDescriptor> PublishedMessages { get; init; } = Array.Empty<TypeDescriptor>();

    /// <summary>External systems on either end of this slice (translation edges).</summary>
    public IReadOnlyList<ExternalSystemDescriptor> ExternalSystems { get; init; } = Array.Empty<ExternalSystemDescriptor>();

    /// <summary>
    /// Hotspots attached to this slice — primarily pending specifications (jasperfx#689), plus any
    /// prose the overlay declared with <c>Hotspot("…")</c> (jasperfx#690). Each one renders as a
    /// <see cref="EventModelElementKind.Hotspot"/> element in the wireframe lane.
    /// </summary>
    public IReadOnlyList<HotspotDescriptor> Hotspots { get; init; } = Array.Empty<HotspotDescriptor>();

    /// <summary>
    /// Specifications bound to this slice by identity + resolved types. Stamped by sources, never
    /// hand-typed. Empty means "no spec" — the orange of drift colouring.
    /// </summary>
    public IReadOnlyList<SpecificationDescriptor> Specifications { get; init; } = Array.Empty<SpecificationDescriptor>();

    /// <summary>
    /// Domain / bounded context the slice belongs to, so large models can collapse into
    /// sub-diagrams. Null when ungrouped.
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// Events this slice's projection, read model or automation <em>applies</em> — what it reads,
    /// as against what <see cref="EmittedEvents"/> says it writes (jasperfx#824).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vocabulary recorded only what a slice produced. A State View slice — event → read model,
    /// across slices — is the arrow people most expect on an Event Modeling board, and nothing in the
    /// descriptor could say "this projection applies <c>AccountOpened</c>". The only place consumption
    /// was recorded at all was <see cref="AggregateDescriptor.AppliedEvents"/>: model level, aggregates
    /// only.
    /// </para>
    /// <para>
    /// Each entry becomes an <see cref="EventModelElementKind.Event"/> element in this slice's own
    /// event-stream lane — the "repeat the sticky where it is consumed" convention, and what a canvas
    /// needs as the <em>To</em> end of an <see cref="EventModelLinkKind.EventConsumed"/> link. An event
    /// both emitted and consumed by one slice is one element, not two.
    /// </para>
    /// </remarks>
    public IReadOnlyList<TypeDescriptor> ConsumedEvents { get; init; } = Array.Empty<TypeDescriptor>();

    /// <summary>
    /// Read models this slice reads <em>before deciding</em> — an Automation's input, a UI's query
    /// (jasperfx#824).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splits the half of <see cref="ReadModelTypes"/> that was overloaded. That member is documented
    /// as "read-model types the slice reads from <em>or</em> produces", and Wolverine folded
    /// <c>[ReadModel]</c>/<c>[Entity]</c> parameters (reads) and <c>IStorageAction&lt;T&gt;</c> returns
    /// (writes) into it alike — so the Automation pattern's input, Event → Read Model → ⚙ Command,
    /// could not be drawn at all: nothing linked a read model <em>to</em> a processor.
    /// </para>
    /// <para>
    /// <see cref="ReadModelTypes"/> keeps its meaning for compatibility and is what a slice
    /// <em>produces</em> once a source has split them; this is what it reads. Each entry becomes a
    /// <see cref="EventModelElementKind.ReadModel"/> element linked into the slice's processor, and
    /// the <em>To</em> end of an <see cref="EventModelLinkKind.ReadModelRead"/> link.
    /// </para>
    /// </remarks>
    public IReadOnlyList<TypeDescriptor> ReadsFrom { get; init; } = Array.Empty<TypeDescriptor>();

    /// <summary>
    /// A named span of slices — the navigation unit above <see cref="Domain"/> (jasperfx#824). Null
    /// when unchaptered.
    /// </summary>
    /// <remarks>
    /// Every Event Modeling tool surveyed uses chapters as the answer to "zoom into a part of the
    /// model": eventmodelers.ai's wide arrow spanning several slices, Miro frames, emlang's
    /// per-chapter documents. <see cref="Domain"/> is a bounded context and does not do that job — a
    /// chapter is a span of the timeline, and one domain has many. Bobcat's emlang import threw the
    /// chapter name away at this boundary because there was nowhere to put it.
    /// </remarks>
    public string? Chapter { get; init; }

    /// <summary>
    /// The aggregate whose stream this slice <em>starts</em>, as against appends to (jasperfx#957). Null
    /// when the slice starts no stream, or when no source has said.
    /// </summary>
    /// <remarks>
    /// A dedicated role because "starts the stream" is a different design decision from "acts on this
    /// aggregate", and the code that implements it differs: a start has no existing stream to load and
    /// has to choose the new stream's id. Wolverine's scaffold command reads it to write a
    /// <c>StartStream</c> instead of a write-model handler. The aggregate also appears in
    /// <see cref="AggregateTypes"/>, so a viewer that knows nothing of this role still draws it.
    /// </remarks>
    public TypeDescriptor? StartsStream { get; init; }

    /// <summary>
    /// Which aggregate's stream each emitted event goes to, for the events a declaration put in a stream
    /// lane (jasperfx#1203). Empty when nothing said, which is every slice a code-derived source produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On an Event Modeling board an event sits in a stream lane, so the board knows which stream it
    /// belongs to. A slice that decides against one aggregate and starts another can't say that with
    /// <see cref="EmittedEvents"/> alone. Each entry names an event in <see cref="EmittedEvents"/> and an
    /// aggregate in <see cref="AggregateTypes"/>.
    /// </para>
    /// <para>
    /// Optional per event. An emitted event with no entry here behaves as it always did, and tooling
    /// (Wolverine's scaffold) falls back to inferring the stream from the aggregates' <c>Apply</c> and
    /// <c>Create</c> methods. Use <see cref="StreamFor"/> to read it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<EmittedEventStream> EmittedEventStreams { get; init; } = Array.Empty<EmittedEventStream>();

    /// <summary>
    /// The aggregate whose stream <paramref name="emittedEvent"/> goes to, or null when no stream lane was
    /// declared for it (jasperfx#1203).
    /// </summary>
    public TypeDescriptor? StreamFor(TypeDescriptor emittedEvent)
        => EmittedEventStreams.FirstOrDefault(x => SameType(x.Event, emittedEvent))?.Aggregate;

    /// <summary>
    /// <em>Why</em> the slice has the aggregate it has, when a declaration said (jasperfx#994): applied
    /// from the definition's <c>ForAggregate</c> default, declared on the slice, deliberately none, or a
    /// DCB decider model instead. Null when nobody said — the code-derived sources never do, since the
    /// code simply has the aggregate it has.
    /// </summary>
    /// <remarks>
    /// Tooling reads this to explain itself: Wolverine's scaffold stops warning about a missing aggregate
    /// on a slice declared <see cref="EventModeling.AggregateDeclaration.None"/>, and writes a TODO for a
    /// <see cref="EventModeling.AggregateDeclaration.DeciderModel"/>. A default-applied aggregate is still
    /// a declared claim on <see cref="AggregateTypes"/>, so code that decides against something else wins
    /// and the difference becomes a hotspot as usual.
    /// </remarks>
    public AggregateDeclaration? AggregateDeclaration { get; init; }

    /// <summary>
    /// The Dynamic Consistency Boundary decider model the slice decides through, rather than single-stream
    /// aggregates (jasperfx#994). Null when the slice has none.
    /// </summary>
    /// <remarks>
    /// Recorded only: how the decider's events are selected — tags, queries — is not modelled yet.
    /// </remarks>
    public TypeDescriptor? DeciderModel { get; init; }

    /// <summary>
    /// How a view slice's read model is projected — single-stream, multi-stream or over a DCB tag query
    /// (JasperFx/wolverine#4865). Null when nothing declared it, which tooling reads as single-stream.
    /// </summary>
    public ViewProjection? ViewProjection { get; init; }

    /// <summary>
    /// The consistency tags each emitted event carries, for the events a source tagged (jasperfx#1297). Empty
    /// when nothing said. Each entry names an event in <see cref="EmittedEvents"/> and tags in the model's
    /// <see cref="EventModelDescriptor.Tags"/>.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="EmittedEventStreams"/>, kept apart from the stream lane: an event belongs to
    /// several boundaries at once, which is also why a canvas draws tag chips on the event rather than a lane
    /// per tag. Declared with <c>Emits&lt;T&gt;().TaggedBy&lt;CourseId&gt;()</c>; the derived rung fills it from
    /// the store's tag registry and tag rules. Use <see cref="TagsFor"/> to read it.
    /// </remarks>
    public IReadOnlyList<EmittedEventTags> EventTags { get; init; } = Array.Empty<EmittedEventTags>();

    /// <summary>The tags <paramref name="emittedEvent"/> carries, or empty when none were recorded (jasperfx#1297).</summary>
    public IReadOnlyList<string> TagsFor(TypeDescriptor emittedEvent)
        => EventTags.FirstOrDefault(x => SameType(x.Event, emittedEvent))?.Tags ?? Array.Empty<string>();

    /// <summary>
    /// What a command slice reads by tag before it decides — a model or a projection, selected by a tag whose
    /// value a command member supplies (jasperfx#1297). Empty on a slice that reads nothing by tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reads declared, boundary derived.</b> A command never declares its own consistency boundary; the query
    /// (<see cref="BoundaryQuery"/>) and the append condition are derived from these reads. Declared with
    /// <c>Reads&lt;TProjection&gt;().By&lt;CourseId&gt;(…)</c> and <c>Decides&lt;TModel&gt;().ByTag&lt;CourseId&gt;(…)</c>;
    /// the derived rung fills it from the decider model's tag rules and the <c>Load()</c> calls Wolverine can see.
    /// </para>
    /// <para>
    /// Not <see cref="ReadsFrom"/>, which is a read model consulted whole. A read here is by tag, and it is what
    /// puts the slice inside a Dynamic Consistency Boundary.
    /// </para>
    /// </remarks>
    public IReadOnlyList<BoundaryReadDescriptor> Reads { get; init; } = Array.Empty<BoundaryReadDescriptor>();

    /// <summary>
    /// The consistency boundary the slice's <see cref="Reads"/> imply: one term per tag read, each with the
    /// emitted events carrying that tag (jasperfx#1297). Computed on every read, like <see cref="Elements"/>, so a
    /// deserializer ignores it and recomputes. Empty when the slice reads nothing by tag.
    /// </summary>
    /// <remarks>
    /// This is the slice's half of the query — what it reads by and what it appends under the same tags. The
    /// events a read model folds are known only at model level; <see cref="EventModelDescriptor.BoundaryQueryFor"/>
    /// adds them from the aggregates' applied events and the producing View slices' consumed events.
    /// </remarks>
    public BoundaryQueryDescriptor BoundaryQuery
    {
        get
        {
            if (Reads.Count == 0) return BoundaryQueryDescriptor.Empty;

            var terms = new List<BoundaryQueryTerm>();
            foreach (var read in Reads)
            {
                if (terms.Any(x => string.Equals(x.Tag, read.Tag, StringComparison.Ordinal))) continue;

                var tagged = EmittedEvents
                    .Where(e => TagsFor(e).Contains(read.Tag, StringComparer.Ordinal))
                    .ToList();

                terms.Add(new BoundaryQueryTerm(read.Tag, read.Member, tagged));
            }

            return new BoundaryQueryDescriptor(terms);
        }
    }

    /// <summary>
    /// Projections this slice declares to be <em>outside</em> its consistency boundary — read models fed by
    /// the slice's events that no decision depends on (jasperfx#1297). Empty when nothing said.
    /// </summary>
    /// <remarks>
    /// The explicit "this one is informational" marker, so a canvas can tell a projection the boundary
    /// deliberately leaves out from one nobody has thought about. Declared with
    /// <c>UntaggedProjection&lt;T&gt;()</c>; the projections themselves also appear in <see cref="ProjectionTypes"/>.
    /// </remarks>
    public IReadOnlyList<TypeDescriptor> UntaggedProjections { get; init; } = Array.Empty<TypeDescriptor>();

    /// <summary>
    /// The ways this command slice can refuse — "Course is full", "Student already enrolled" (jasperfx#1297).
    /// Empty when nothing said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filled by declaration (<c>Refuses("Course is full")</c>), by import (a board's <c>errorDescription</c>,
    /// emlang's <c>x:</c>), and by the <see cref="EventModelProvenance.Specified"/> rung from generated specs.
    /// The <see cref="EventModelProvenance.Derived"/> rung never fills it: that would mean reading method
    /// bodies, so no false disagreements arise against the code.
    /// </para>
    /// <para>
    /// Reasons are vocabulary, not competing facts, so <see cref="Merge"/> unions them whatever the rungs. A
    /// specification refusing with a reason the declared list lacks is a
    /// <see cref="HotspotOrigin.UndeclaredRejection"/> hotspot.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> RejectionReasons { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Who triggers the slice: an actor by name, with the roles the slice requires of them (jasperfx#1298).
    /// Null when nobody said.
    /// </summary>
    /// <remarks>
    /// Declared with <c>Actor("Volunteer")</c>, or implied by a role verb after <c>TriggeredBy(label, Human)</c>;
    /// filled on import from actor lanes; the HTTP source fills required roles from authorization metadata.
    /// Declared and derived required roles may disagree, which <see cref="Merge"/> records as a real finding.
    /// </remarks>
    public ActorDescriptor? Actor { get; init; }

    /// <summary>
    /// Which of the <see cref="ConsumedEvents"/> come from an external system, and which one (jasperfx#1298).
    /// Empty when none do. Each entry names an event in <see cref="ConsumedEvents"/> and a system in
    /// <see cref="ExternalSystems"/>.
    /// </summary>
    /// <remarks>
    /// Declared with <c>From&lt;T&gt;().External("Payments")</c>; the derived rung sets it when the consumed type
    /// arrives on a non-local endpoint and no slice in the model emits it. A canvas draws the event on the stream
    /// row with the external system's name instead of an aggregate. Use <see cref="ExternalSystemFor"/> to read it.
    /// </remarks>
    public IReadOnlyList<ExternalEventDescriptor> ExternalEvents { get; init; } = Array.Empty<ExternalEventDescriptor>();

    /// <summary>The external system <paramref name="consumedEvent"/> comes from, or null when it is the model's own (jasperfx#1298).</summary>
    public string? ExternalSystemFor(TypeDescriptor consumedEvent)
        => ExternalEvents.FirstOrDefault(x => SameType(x.Event, consumedEvent))?.ExternalSystem;

    /// <summary>
    /// The cadence of an automation fired by the scheduler — every so often, a cron expression, or a named
    /// schedule — beside <see cref="EventModeling.TriggerKind.JobScheduler"/> (jasperfx#1298). Null when nobody said.
    /// </summary>
    /// <remarks>
    /// Declared with <c>Every(TimeSpan)</c>, <c>OnCron("…")</c> or <c>Scheduled("name")</c>; the derived rung fills
    /// it from registered recurring schedules. A canvas shows it on the automation's card.
    /// </remarks>
    public ScheduleDescriptor? Schedule { get; init; }

    /// <summary>
    /// The name of the screen a human trigger acts through — "Checkout page" — and nothing more (jasperfx#1298).
    /// Null when nobody said.
    /// </summary>
    /// <remarks>
    /// A name only: no content, images or HTML. Declared with <c>FromScreen("…")</c>; import keeps the board's
    /// screen title. Renders as a <see cref="EventModelElementKind.Screen"/> element in the wireframe lane,
    /// beside the trigger.
    /// </remarks>
    public string? Screen { get; init; }

    /// <summary>
    /// Plain text or markdown describing the slice (jasperfx#1298). Null when none.
    /// </summary>
    /// <remarks>
    /// Declared with <c>Describe("…")</c>; import fills it from board text; the derived rung fills it from XML doc
    /// comments. On a merge the richer text wins and nothing is recorded as a disagreement: two descriptions are
    /// two drafts of one paragraph, not two claims about the system.
    /// </remarks>
    public string? Description { get; init; }

    /// <summary>
    /// Descriptions of the elements this slice references, by type (jasperfx#1298). Empty when none. Declared with
    /// <c>Describe&lt;T&gt;("…")</c>; merged as <see cref="Description"/> is, element by element.
    /// </summary>
    public IReadOnlyList<ElementDescription> ElementDescriptions { get; init; } = Array.Empty<ElementDescription>();

    /// <summary>The description recorded for <paramref name="type"/>, or null (jasperfx#1298).</summary>
    public string? DescriptionOf(TypeDescriptor type)
        => ElementDescriptions.FirstOrDefault(x => SameType(x.Type, type))?.Text;

    /// <summary>
    /// Whether any claim on this slice came from <paramref name="rung"/>: its summary <see cref="Provenance"/>
    /// (an unattributed slice reads <see cref="EventModelProvenance.Declared"/>) or any entry of
    /// <see cref="ClaimedBy"/>. What the information-completeness check asks before raising anything
    /// (jasperfx#1299): a purely derived model gets no questions.
    /// </summary>
    public bool HasRung(EventModelProvenance rung)
        => (Provenance ?? EventModelProvenance.Declared) == rung || ClaimedBy.Values.Contains(rung);

    /// <summary>
    /// <em>Which</em> source produced this slice, as against <see cref="Provenance"/>'s <em>what rung
    /// it sits on</em> (jasperfx#836). The contributing source's <c>IEventModelDefinitionSource.Subject</c>,
    /// or for the store-derived rung the store's own <c>EventStoreUsage.SubjectUri</c>. Null when the
    /// source did not attribute itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a merge key.</b> <see cref="Name"/> stays the only one, and that is the point: two
    /// sources describing one slice must still fold into one. This records who contributed, so that
    /// when a merge <em>does</em> drop a claim the hotspot can name the store it came from rather
    /// than only the rung — "Derived claims X; Derived claims Y" is unactionable when both rungs read
    /// the same.
    /// </para>
    /// <para>
    /// <b>Why the store dimension was missing.</b> The telemetry half of the stack has carried full
    /// store attribution all along — CritterWatch keys a shard fact on
    /// <c>(service, storeUri, database, tenant, shard)</c>. The modelling half carried none, so in a
    /// modular monolith where two modules each register an ancillary store, two documents of the same
    /// simple name — an <c>AuditEntry</c>, a <c>Summary</c> — were indistinguishable once they reached
    /// a descriptor.
    /// </para>
    /// <para>
    /// Useful attribution needs the store subjects to be genuinely per-store, which is a store-side
    /// prerequisite rather than something this member can enforce (fisher#279, marten#5409).
    /// </para>
    /// </remarks>
    public Uri? Origin { get; init; }

    /// <summary>
    /// Which rung of the provenance ladder the source that produced this slice sits on
    /// (jasperfx#703). Null means unattributed, which <see cref="ProvenanceFor"/> reads as
    /// <see cref="EventModelProvenance.Declared"/> — so a model whose sources have not been stamped
    /// yet merges exactly as it did before, on registration order.
    /// </summary>
    /// <remarks>
    /// On a slice assembled by <see cref="Merge"/> this is the highest rung that contributed
    /// anything. It is a summary for viewers that do not care which role came from where;
    /// <see cref="ClaimedBy"/> is the per-role truth.
    /// </remarks>
    public EventModelProvenance? Provenance { get; init; }

    /// <summary>
    /// Per-role attribution: which rung claimed each role of this slice. Stamped by
    /// <see cref="Merge"/>, which is the only place the answer can differ role by role.
    /// </summary>
    /// <remarks>
    /// Empty on a slice straight from one source — there is nothing to disambiguate, so
    /// <see cref="ProvenanceFor"/> derives the answer from <see cref="Provenance"/> and whether the
    /// role is claimed at all. Prefer <see cref="ProvenanceFor"/> over reading this directly.
    /// </remarks>
    public IReadOnlyDictionary<EventModelRole, EventModelProvenance> ClaimedBy { get; init; }
        = new Dictionary<EventModelRole, EventModelProvenance>();

    /// <summary>
    /// The rung that claimed <paramref name="role"/> on this slice, or null when nothing claims it.
    /// </summary>
    /// <remarks>
    /// This is the acceptance criterion of jasperfx#703 in one method: after a merge of three
    /// sources, ask any role which source's claim survived.
    /// </remarks>
    public EventModelProvenance? ProvenanceFor(EventModelRole role)
    {
        if (ClaimedBy.TryGetValue(role, out var claimed)) return claimed;

        return Claims(role) ? Provenance ?? EventModelProvenance.Declared : null;
    }

    /// <summary>
    /// Does this slice carry a value for <paramref name="role"/>? A non-null scalar or a non-empty
    /// list. Structural on purpose: no source has to opt into being attributed.
    /// </summary>
    public bool Claims(EventModelRole role) => role switch
    {
        EventModelRole.TriggerLabel => TriggerLabel is not null,
        EventModelRole.TriggerType => TriggerType is not null,
        EventModelRole.TriggerKind => TriggerKind is not null,
        EventModelRole.TriggerOrigin => TriggerOrigin is not null,
        EventModelRole.TriggeredBy => TriggeredBy is not null,
        EventModelRole.Pattern => Pattern is not null,
        EventModelRole.CommandType => CommandType is not null,
        EventModelRole.HandlerType => HandlerType is not null,
        EventModelRole.AggregateTypes => AggregateTypes.Count > 0,
        EventModelRole.EmittedEvents => EmittedEvents.Count > 0,
        EventModelRole.PublishedMessages => PublishedMessages.Count > 0,
        EventModelRole.ProjectionTypes => ProjectionTypes.Count > 0,
        EventModelRole.ReadModelTypes => ReadModelTypes.Count > 0,
        EventModelRole.ExternalSystems => ExternalSystems.Count > 0,
        EventModelRole.Hotspots => Hotspots.Count > 0,
        EventModelRole.Specifications => Specifications.Count > 0,
        EventModelRole.Domain => Domain is not null,
        EventModelRole.ConsumedEvents => ConsumedEvents.Count > 0,
        EventModelRole.ReadsFrom => ReadsFrom.Count > 0,
        EventModelRole.Chapter => Chapter is not null,
        EventModelRole.Origin => Origin is not null,
        EventModelRole.StartsStream => StartsStream is not null,
        EventModelRole.AggregateDeclaration => AggregateDeclaration is not null,
        EventModelRole.DeciderModel => DeciderModel is not null,
        EventModelRole.ViewProjection => ViewProjection is not null,
        EventModelRole.EventTags => EventTags.Count > 0,
        EventModelRole.Reads => Reads.Count > 0,
        EventModelRole.UntaggedProjections => UntaggedProjections.Count > 0,
        EventModelRole.RejectionReasons => RejectionReasons.Count > 0,
        EventModelRole.Actor => Actor is not null,
        EventModelRole.ExternalEvents => ExternalEvents.Count > 0,
        EventModelRole.Schedule => Schedule is not null,
        EventModelRole.Screen => Screen is not null,
        EventModelRole.Description => Description is not null || ElementDescriptions.Count > 0,
        _ => false,
    };

    /// <summary>
    /// Stamp this slice with the rung of the source that produced it, if it is not already
    /// attributed. A source that stamps its own slices individually is left alone.
    /// </summary>
    public EventModelSliceDescriptor WithProvenance(EventModelProvenance provenance)
        => Provenance is null ? this with { Provenance = provenance } : this;

    /// <summary>
    /// The rendering contract — every element of the slice with a stable id, a kind (→ colour)
    /// and a lane. Computed from the typed roles on each read.
    /// </summary>
    public IReadOnlyList<EventModelElement> Elements => buildGraph().elements;

    /// <summary>
    /// The explicit, directed relationships between <see cref="Elements"/>, by element id.
    /// Computed from the typed roles on each read.
    /// </summary>
    public IReadOnlyList<EventModelEdge> Edges => buildGraph().edges;

    /// <summary>
    /// Roles this slice carries that its <see cref="Pattern"/> rules out, as
    /// <see cref="HotspotOrigin.PatternViolation"/> hotspots (jasperfx#1003). Computed from the roles on
    /// every read, like <see cref="Elements"/>, so a deserializer ignores it and recomputes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Today the one rule is <see cref="SlicePattern.Query"/>: a query changes no state, so a Query slice
    /// that emits events (<see cref="EmittedEvents"/>), starts a stream (<see cref="StartsStream"/>) or
    /// sends a command (<see cref="PublishedMessages"/>) gets one hotspot per offending role.
    /// <see cref="ConsumedEvents"/> is not a violation: reading an aggregate by live aggregation consumes
    /// its events without changing anything.
    /// </para>
    /// <para>
    /// Kept apart from <see cref="Hotspots"/>, which is stored and unioned by <see cref="Merge"/>: a
    /// violation is a fact about the merged roles, so it is evaluated after the merge and retires itself
    /// when the slice is fixed. Each one also renders as a <see cref="EventModelElementKind.Hotspot"/>
    /// element in the wireframe lane.
    /// </para>
    /// </remarks>
    public IReadOnlyList<HotspotDescriptor> PatternViolations
    {
        get
        {
            if (Pattern != SlicePattern.Query) return Array.Empty<HotspotDescriptor>();

            var violations = new List<HotspotDescriptor>();

            if (EmittedEvents.Count > 0)
            {
                violations.Add(HotspotDescriptor.PatternViolation(SlicePattern.Query, EventModelRole.EmittedEvents,
                    render(EmittedEvents, x => x.Name)));
            }

            if (StartsStream is not null)
            {
                violations.Add(HotspotDescriptor.PatternViolation(SlicePattern.Query, EventModelRole.StartsStream,
                    StartsStream.Name));
            }

            if (PublishedMessages.Count > 0)
            {
                violations.Add(HotspotDescriptor.PatternViolation(SlicePattern.Query, EventModelRole.PublishedMessages,
                    render(PublishedMessages, x => x.Name)));
            }

            return violations;
        }
    }

    /// <summary>
    /// Fold another source's view of the <em>same</em> slice into this one, role by role, with the
    /// higher rung of <see cref="EventModelProvenance"/> winning (jasperfx#703).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per claimed role, not wholesale.</b> A source that does not claim a role never overrides
    /// one that does, whatever rung it sits on. That is why slice names, domains and specification
    /// links keep coming from declarations: nothing else claims them, so nothing else can take them.
    /// </para>
    /// <para>
    /// <b>Ties fall back to first-wins</b>, which is what this method did for every role before
    /// jasperfx#703 — so two unattributed sources merge exactly as they always have, on the order
    /// they were given. That is the compatibility hinge: a model whose sources have not been stamped
    /// yet is byte-identical to what it produced before.
    /// </para>
    /// <para>
    /// <b>A higher rung replaces a list, it does not union with it.</b> Unioning is what made the old
    /// merge lossy in the other direction: derived <c>{A, C}</c> unioned with observed <c>{A, B}</c>
    /// silently invents a slice that emits three events and nobody ever claimed. Production winning
    /// means the answer is <c>{A, B}</c>; what happened to <c>C</c> is a finding, and jasperfx#704
    /// records it rather than letting it vanish. Same-rung lists still union in order, deduplicated
    /// by identity, exactly as before.
    /// </para>
    /// <para>
    /// <b>A dropped claim becomes a hotspot</b> (jasperfx#704). Whenever both sides claim a role and
    /// the merged answer does not contain the other side's claim, a
    /// <see cref="HotspotOrigin.SourceDisagreement"/> hotspot is appended naming the role, both
    /// claims and the rung each came from. That covers a higher rung replacing a list <em>and</em>
    /// two same-rung sources disagreeing on a scalar, where first-wins has always silently dropped
    /// the loser. Nothing is recorded when nothing is lost — same-rung lists union, so a model with
    /// no disagreements is identical to one produced before jasperfx#704.
    /// </para>
    /// <para>
    /// <b>An HTTP endpoint and a message handler for one command are one exception</b> (jasperfx#1099).
    /// Two <see cref="EventModelProvenance.Derived"/> sources claiming
    /// <see cref="EventModeling.TriggerKind.Http"/> and <see cref="EventModeling.TriggerKind.MessageHandler"/>
    /// describe two ways into one slice, not a dispute: whichever came first,
    /// <see cref="TriggerKind"/>, <see cref="HandlerType"/>, <see cref="HandlerMethod"/> and
    /// <see cref="Origin"/> keep the HTTP side, the message handler moves to
    /// <see cref="AdditionalEntryPoints"/>, and those roles raise nothing. Every other role merges as usual.
    /// </para>
    /// <para>
    /// <b><see cref="Hotspots"/> itself is unioned, never arbitrated.</b> Hotspots are annotations,
    /// not factual claims about the system, and letting a higher-rung source's hotspot list replace a
    /// lower one would discard exactly the findings this feature exists to record — including the
    /// disagreements a previous merge already found.
    /// </para>
    /// </remarks>
    public EventModelSliceDescriptor Merge(EventModelSliceDescriptor other)
    {
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cannot merge slice '{other.Name}' into slice '{Name}'; slices merge by name.",
                nameof(other));
        }

        // jasperfx#954. One source, one rung, two different handlers under one name: these are two
        // slices that collided on the key, not two views of one slice. Folding them is what attributed
        // one handler's output to another; keep the first, say so, and fold nothing else in.
        if (collidesWith(other))
        {
            var collision = HotspotDescriptor.SliceCollision(Name,
                new EventModelClaim(ProvenanceFor(EventModelRole.HandlerType)!.Value, HandlerType!.FullName,
                    Origin?.OriginalString),
                new EventModelClaim(other.ProvenanceFor(EventModelRole.HandlerType)!.Value,
                    other.HandlerType!.FullName, other.Origin?.OriginalString));

            return this with
            {
                Hotspots = union(union(Hotspots, other.Hotspots, hotspotKey), [collision], hotspotKey),
            };
        }

        var claimedBy = new Dictionary<EventModelRole, EventModelProvenance>();
        var disagreements = new List<HotspotDescriptor>();

        var foldsEntryPoints = foldsEntryPointsWith(other);

        // jasperfx#1099. The roles that name one entry point. Tied in the fold, none of them is a dropped
        // claim, only the second of two true answers, and the rung ladder still decides any that aren't tied.
        bool settledByFold(EventModelRole role)
            => foldsEntryPoints
               && role is EventModelRole.TriggerKind or EventModelRole.HandlerType or EventModelRole.Origin
               && ProvenanceFor(role) == other.ProvenanceFor(role);

        // True when other's claim on this role outranks ours -- or when we make no claim at all.
        // False on a tie, which is what preserves the pre-jasperfx#703 first-wins behaviour, except that
        // a tie the fold settles goes to the HTTP side so the answer does not depend on source order.
        bool theirsWins(EventModelRole role)
        {
            var mine = ProvenanceFor(role);
            var theirs = other.ProvenanceFor(role);

            return theirs is not null
                   && (mine is null || theirs.Value.Outranks(mine.Value)
                                    || (settledByFold(role) && other.TriggerKind == EventModeling.TriggerKind.Http));
        }

        bool takeOther(EventModelRole role)
        {
            var takeTheirs = theirsWins(role);

            if ((takeTheirs ? other.ProvenanceFor(role) : ProvenanceFor(role)) is { } rung) claimedBy[role] = rung;

            return takeTheirs;
        }

        // Both sides claimed the role and the merge kept only one of them. Record what was lost.
        void disagree(EventModelRole role, bool tookTheirs, string mineValue, string theirsValue)
        {
            if (settledByFold(role)) return;

            // jasperfx#859. Each claim names the source that made it, not only the rung it sits on:
            // a rung can hold several sources, and spec-first work having a declared model file AND
            // specs -- both Declared by construction -- is the normal case rather than an edge one.
            // The exception is the Origin role itself, whose VALUE already is the source; naming it
            // twice would render "store://ledger claims store://ledger".
            var source = role == EventModelRole.Origin ? null : Origin?.OriginalString;
            var otherSource = role == EventModelRole.Origin ? null : other.Origin?.OriginalString;

            var mineClaim = new EventModelClaim(ProvenanceFor(role)!.Value, mineValue, source);
            var theirsClaim = new EventModelClaim(other.ProvenanceFor(role)!.Value, theirsValue, otherSource);

            disagreements.Add(tookTheirs
                ? HotspotDescriptor.SourceDisagreement(role, theirsClaim, mineClaim)
                : HotspotDescriptor.SourceDisagreement(role, mineClaim, theirsClaim));
        }

        T? mergeScalar<T>(EventModelRole role, T? mine, T? theirs, Func<T, string> display) where T : class
        {
            var tookTheirs = takeOther(role);

            if (Claims(role) && other.Claims(role))
            {
                var mineValue = display(mine!);
                var theirsValue = display(theirs!);
                if (!string.Equals(mineValue, theirsValue, StringComparison.Ordinal))
                {
                    disagree(role, tookTheirs, mineValue, theirsValue);
                }
            }

            return tookTheirs ? theirs : mine;
        }

        T? mergeValue<T>(EventModelRole role, T? mine, T? theirs) where T : struct
        {
            var tookTheirs = takeOther(role);

            if (Claims(role) && other.Claims(role) && !Equals(mine!.Value, theirs!.Value))
            {
                disagree(role, tookTheirs, mine.Value.ToString()!, theirs.Value.ToString()!);
            }

            return tookTheirs ? theirs : mine;
        }

        IReadOnlyList<T> mergeList<T>(EventModelRole role, IReadOnlyList<T> mine, IReadOnlyList<T> theirs,
            Func<T, string> key, Func<T, string> display, Func<T, string>? ifIndistinguishable = null)
        {
            var myRung = ProvenanceFor(role);
            var theirRung = other.ProvenanceFor(role);

            var tookTheirs = takeOther(role);

            if (theirRung is null) return mine;
            if (myRung is null) return theirs;

            // Same rung: the union keeps both claims, so nothing was lost and nothing disagreed.
            if (myRung == theirRung) return union(mine, theirs, key);

            if (!sameSet(mine, theirs, key))
            {
                var mineValue = render(mine, display);
                var theirsValue = render(theirs, display);

                if (mineValue == theirsValue && ifIndistinguishable is not null)
                {
                    mineValue = render(mine, ifIndistinguishable);
                    theirsValue = render(theirs, ifIndistinguishable);
                }

                disagree(role, tookTheirs, mineValue, theirsValue);
            }

            return tookTheirs ? theirs : mine;
        }

        // jasperfx#954. A type scalar disagrees by identity, not by simple name: two handlers both called
        // OrderPlacedHandler in different namespaces are different claims, and comparing Name alone
        // dropped one of them without a hotspot. The rendering stays the simple name unless that is
        // the only thing the two have in common.
        TypeDescriptor? mergeType(EventModelRole role, TypeDescriptor? mine, TypeDescriptor? theirs)
        {
            var tookTheirs = takeOther(role);

            if (Claims(role) && other.Claims(role) && !SameType(mine!, theirs!))
            {
                var sameName = string.Equals(mine!.Name, theirs!.Name, StringComparison.Ordinal);
                disagree(role, tookTheirs, sameName ? mine.FullName : mine.Name,
                    sameName ? theirs.FullName : theirs.Name);
            }

            return tookTheirs ? theirs : mine;
        }

        // Key on FullName only when BOTH sides have a real one. A declared type does not exist
        // yet, so its FullName is synthesized from the model's single namespace — and the code it
        // describes puts types in whatever namespaces it likes, typically one per domain. Keying
        // on a guess made `CritterCrush.Appointment` and `CritterCrush.Appointments.Appointment`
        // disagree, which is a thing disagreeing with itself (jasperfx#798). An empty
        // AssemblyName is exactly the marker for "this is a declaration, not a type".
        IReadOnlyList<TypeDescriptor> mergeTypes(EventModelRole role, IReadOnlyList<TypeDescriptor> mine,
            IReadOnlyList<TypeDescriptor> theirs)
        {
            var declared = mine.Concat(theirs).Any(x => string.IsNullOrEmpty(x.AssemblyName));

            return mergeList(role, mine, theirs,
                key: declared ? x => x.Name : x => x.FullName,
                display: x => x.Name,
                // Simple names are what a reader wants, right up until both sides render the same
                // string — "Derived claims Appointment; Declared claims Appointment" says nothing.
                // That is exactly when the full name is the only thing that distinguishes them.
                ifIndistinguishable: x => x.FullName);
        }

        var triggerLabel = mergeScalar(EventModelRole.TriggerLabel, TriggerLabel, other.TriggerLabel, x => x);
        var triggerType = mergeType(EventModelRole.TriggerType, TriggerType, other.TriggerType);
        var commandType = mergeType(EventModelRole.CommandType, CommandType, other.CommandType);
        var handlerType = mergeType(EventModelRole.HandlerType, HandlerType, other.HandlerType);
        var handlerMethod = mergeHandlerMethod();

        // The method is detail of the handler type, as a route name is of the origin: it travels with the
        // winning handler, a known method fills in an unknown one when both sides name the same type, and
        // two different methods on the same type disagree. A different type already disagreed above.
        string? mergeHandlerMethod()
        {
            var winnerIsMine = !theirsWins(EventModelRole.HandlerType);
            var winning = winnerIsMine ? HandlerMethod : other.HandlerMethod;
            var losing = winnerIsMine ? other.HandlerMethod : HandlerMethod;

            if (HandlerType is null || other.HandlerType is null || !SameType(HandlerType, other.HandlerType))
            {
                return winning;
            }

            if (winning is null) return losing;

            if (losing is not null && !string.Equals(winning, losing, StringComparison.Ordinal))
            {
                disagree(EventModelRole.HandlerType, !winnerIsMine,
                    $"{HandlerType.Name}.{HandlerMethod}", $"{other.HandlerType.Name}.{other.HandlerMethod}");
            }

            return winning;
        }
        var emittedEvents = mergeTypes(EventModelRole.EmittedEvents, EmittedEvents, other.EmittedEvents);
        var projectionTypes = mergeTypes(EventModelRole.ProjectionTypes, ProjectionTypes, other.ProjectionTypes);
        var readModelTypes = mergeTypes(EventModelRole.ReadModelTypes, ReadModelTypes, other.ReadModelTypes);
        var pattern = mergeValue(EventModelRole.Pattern, Pattern, other.Pattern);
        var (triggerKind, triggeredBy) = mergeTrigger();

        // Who versus how. A declared Human met by a kind a person can reach code through (Http, Grpc,
        // MessageHandler) is not a disagreement: Human answers who triggers the slice, the derived kind
        // how the code receives it. Keep both -- the how on TriggerKind, so viewers that label a trigger by
        // it keep working, and the who on TriggeredBy. Each role is attributed to the side that claimed it.
        (TriggerKind?, TriggerKind?) mergeTrigger()
        {
            if (TriggerKind is not { } mine || other.TriggerKind is not { } theirs
                || !TriggerKindCompatibility.SplitsWhoFromHow(mine, theirs))
            {
                return (mergeValue(EventModelRole.TriggerKind, TriggerKind, other.TriggerKind),
                    mergeValue(EventModelRole.TriggeredBy, TriggeredBy, other.TriggeredBy));
            }

            var mineIsWho = mine == EventModeling.TriggerKind.Human;
            claimedBy[EventModelRole.TriggerKind] =
                (mineIsWho ? other.ProvenanceFor(EventModelRole.TriggerKind) : ProvenanceFor(EventModelRole.TriggerKind))!.Value;

            // A side that already split who from how in an earlier merge brings its TriggeredBy along.
            var who = mergeValue(EventModelRole.TriggeredBy, TriggeredBy, other.TriggeredBy);
            if (who is null)
            {
                who = EventModeling.TriggerKind.Human;
                claimedBy[EventModelRole.TriggeredBy] =
                    (mineIsWho ? ProvenanceFor(EventModelRole.TriggerKind) : other.ProvenanceFor(EventModelRole.TriggerKind))!.Value;
            }

            return (mineIsWho ? theirs : mine, who);
        }
        var triggerOrigin = mergeScalar(EventModelRole.TriggerOrigin, TriggerOrigin, other.TriggerOrigin,
            x => x.Label ?? x.ToString());

        // A route name is optional detail on the origin, so it merges like a value of its own: a known name
        // beats an unknown one whichever origin won, and two known names that differ disagree -- unless the
        // origins already disagreed above, which recorded the loser whole.
        if (TriggerOrigin is not null && other.TriggerOrigin is not null && triggerOrigin is not null)
        {
            var loser = ReferenceEquals(triggerOrigin, TriggerOrigin) ? other.TriggerOrigin : TriggerOrigin;
            var originsAgree = string.Equals(TriggerOrigin.Label ?? TriggerOrigin.ToString(),
                other.TriggerOrigin.Label ?? other.TriggerOrigin.ToString(), StringComparison.Ordinal);

            if (triggerOrigin.HttpRouteName is null && loser.HttpRouteName is not null
                && (triggerOrigin.HttpRoute is null || loser.HttpRoute is null
                    || string.Equals(triggerOrigin.HttpRoute, loser.HttpRoute, StringComparison.Ordinal)))
            {
                triggerOrigin = triggerOrigin with { HttpRouteName = loser.HttpRouteName };
            }
            else if (originsAgree && triggerOrigin.HttpRouteName is not null && loser.HttpRouteName is not null
                     && !string.Equals(triggerOrigin.HttpRouteName, loser.HttpRouteName, StringComparison.Ordinal))
            {
                disagree(EventModelRole.TriggerOrigin, !ReferenceEquals(triggerOrigin, TriggerOrigin),
                    $"route name {TriggerOrigin.HttpRouteName}", $"route name {other.TriggerOrigin.HttpRouteName}");
            }
        }
        var aggregateTypes = mergeTypes(EventModelRole.AggregateTypes, AggregateTypes, other.AggregateTypes);
        var publishedMessages = mergeTypes(EventModelRole.PublishedMessages, PublishedMessages, other.PublishedMessages);
        var externalSystems = mergeList(EventModelRole.ExternalSystems, ExternalSystems, other.ExternalSystems,
            x => $"{x.Direction}:{x.Name}", x => x.Name);
        var specifications = mergeList(EventModelRole.Specifications, Specifications, other.Specifications,
            x => x.Identity, x => x.Identity);
        var domain = mergeScalar(EventModelRole.Domain, Domain, other.Domain, x => x);

        // jasperfx#824. The two new lists merge as every other type list does; Chapter merges as
        // Domain does -- first non-null wins on a tie, and a genuine disagreement becomes a
        // SourceDisagreement hotspot rather than a silent drop.
        var consumedEvents = mergeTypes(EventModelRole.ConsumedEvents, ConsumedEvents, other.ConsumedEvents);
        var readsFrom = mergeTypes(EventModelRole.ReadsFrom, ReadsFrom, other.ReadsFrom);
        var chapter = mergeScalar(EventModelRole.Chapter, Chapter, other.Chapter, x => x);

        // jasperfx#957. Compared by type identity, like every other type scalar.
        var startsStream = mergeType(EventModelRole.StartsStream, StartsStream, other.StartsStream);

        // jasperfx#994. Only declarations claim these, so in practice they pass straight through a merge
        // with the code; two definitions disagreeing still leave a hotspot like any other scalar.
        var aggregateDeclaration = mergeValue(EventModelRole.AggregateDeclaration, AggregateDeclaration,
            other.AggregateDeclaration);
        var deciderModel = mergeType(EventModelRole.DeciderModel, DeciderModel, other.DeciderModel);
        var viewProjection = mergeValue(EventModelRole.ViewProjection, ViewProjection, other.ViewProjection);

        // jasperfx#1203. Only a declaration names a stream lane, so lanes union rather than compete: the
        // code says which events are emitted and the declaration says where they go. When two sources put
        // the same event in different lanes, the side that wins EmittedEvents keeps its lane and the other
        // is recorded as a disagreement. A lane whose event is no longer emitted after the merge is dropped.
        var emittedEventStreams = mergeEmittedEventStreams();

        IReadOnlyList<EmittedEventStream> mergeEmittedEventStreams()
        {
            if (EmittedEventStreams.Count == 0 && other.EmittedEventStreams.Count == 0)
            {
                return Array.Empty<EmittedEventStream>();
            }

            var tookTheirs = theirsWins(EventModelRole.EmittedEvents);
            var winning = tookTheirs ? other.EmittedEventStreams : EmittedEventStreams;
            var losing = tookTheirs ? EmittedEventStreams : other.EmittedEventStreams;

            var merged = winning.ToList();
            foreach (var lane in losing)
            {
                var existing = merged.FirstOrDefault(x => SameType(x.Event, lane.Event));
                if (existing is null)
                {
                    merged.Add(lane);
                }
                else if (!SameType(existing.Aggregate, lane.Aggregate)
                         && Claims(EventModelRole.EmittedEvents) && other.Claims(EventModelRole.EmittedEvents))
                {
                    var mine = tookTheirs ? lane : existing;
                    var theirs = tookTheirs ? existing : lane;
                    disagree(EventModelRole.EmittedEvents, tookTheirs,
                        $"{mine.Event.Name} on the {mine.Aggregate.Name} stream",
                        $"{theirs.Event.Name} on the {theirs.Aggregate.Name} stream");
                }
            }

            return merged.Where(x => emittedEvents.Any(e => SameType(e, x.Event))).ToList();
        }

        // jasperfx#1297. Tags follow the events they sit on, as the stream lanes do: the side that wins
        // EmittedEvents keeps its tags, the other side's tags on events it alone tagged are kept too, and two
        // sources tagging one event differently union on the same rung and disagree across rungs -- rendered
        // like AggregateIdentity, naming the event. Tags on an event no longer emitted go with it.
        var eventTags = mergeEventTags();

        IReadOnlyList<EmittedEventTags> mergeEventTags()
        {
            if (EventTags.Count == 0 && other.EventTags.Count == 0) return Array.Empty<EmittedEventTags>();

            var tookTheirs = theirsWins(EventModelRole.EmittedEvents);
            var winning = tookTheirs ? other.EventTags : EventTags;
            var losing = tookTheirs ? EventTags : other.EventTags;
            var sameRung = ProvenanceFor(EventModelRole.EmittedEvents) == other.ProvenanceFor(EventModelRole.EmittedEvents);

            if (Claims(EventModelRole.EventTags) || other.Claims(EventModelRole.EventTags))
            {
                claimedBy[EventModelRole.EventTags] = (Claims(EventModelRole.EventTags) && other.Claims(EventModelRole.EventTags)
                    ? (tookTheirs ? other.ProvenanceFor(EventModelRole.EmittedEvents) : ProvenanceFor(EventModelRole.EmittedEvents))
                    : Claims(EventModelRole.EventTags) ? ProvenanceFor(EventModelRole.EventTags) : other.ProvenanceFor(EventModelRole.EventTags))
                    ?? EventModelProvenance.Declared;
            }

            var merged = winning.ToList();
            foreach (var tags in losing)
            {
                var index = merged.FindIndex(x => SameType(x.Event, tags.Event));
                if (index < 0)
                {
                    merged.Add(tags);
                    continue;
                }

                var existing = merged[index];
                if (sameSet(existing.Tags, tags.Tags, x => x)) continue;

                if (sameRung || !Claims(EventModelRole.EmittedEvents) || !other.Claims(EventModelRole.EmittedEvents))
                {
                    merged[index] = existing with { Tags = union(existing.Tags, tags.Tags, x => x) };
                    continue;
                }

                var mine = tookTheirs ? tags : existing;
                var theirs = tookTheirs ? existing : tags;
                disagree(EventModelRole.EventTags, tookTheirs,
                    $"{mine.Event.Name} tagged {render(mine.Tags, x => x)}",
                    $"{theirs.Event.Name} tagged {render(theirs.Tags, x => x)}");
            }

            return merged.Where(x => emittedEvents.Any(e => SameType(e, x.Event))).ToList();
        }

        // jasperfx#1297. A read is one claim -- this type, by this tag, from this member -- so reads merge as
        // the other lists do: same rung unions, a higher rung replaces and records what it dropped.
        var reads = mergeList(EventModelRole.Reads, Reads, other.Reads,
            x => $"{x.Kind}:{(string.IsNullOrEmpty(x.Type.AssemblyName) ? x.Type.Name : x.Type.FullName)}:{x.Tag}:{x.Member}",
            x => $"{x.Type.Name} by {x.Tag}");
        var untaggedProjections = mergeTypes(EventModelRole.UntaggedProjections, UntaggedProjections, other.UntaggedProjections);

        // jasperfx#1297. Reasons are vocabulary, not competing facts: they union whatever the rungs. What IS
        // recorded is a specification refusing for a reason the declared list lacks -- the model not knowing
        // something the specs do. Only against a non-empty declared list: a model that declares no reasons has
        // not said anything the specs could contradict.
        takeOther(EventModelRole.RejectionReasons);
        var rejectionReasons = union(RejectionReasons, other.RejectionReasons, x => x);
        if (Claims(EventModelRole.RejectionReasons) && other.Claims(EventModelRole.RejectionReasons))
        {
            claimedBy[EventModelRole.RejectionReasons] = higher(ProvenanceFor(EventModelRole.RejectionReasons),
                other.ProvenanceFor(EventModelRole.RejectionReasons))!.Value;

            var (declared, specified) = (ProvenanceFor(EventModelRole.RejectionReasons), other.ProvenanceFor(EventModelRole.RejectionReasons)) switch
            {
                (EventModelProvenance.Declared, EventModelProvenance.Specified) => (RejectionReasons, other.RejectionReasons),
                (EventModelProvenance.Specified, EventModelProvenance.Declared) => (other.RejectionReasons, RejectionReasons),
                _ => (null, null),
            };

            if (declared is not null && specified is not null)
            {
                foreach (var reason in specified)
                {
                    if (declared.Contains(reason, StringComparer.Ordinal)) continue;
                    disagreements.Add(HotspotDescriptor.UndeclaredRejection(reason));
                }
            }
        }

        // jasperfx#1298. The actor is one claim, name and roles together: a different name or a different set of
        // required roles is a real finding, which is why the roles render inside the value.
        var actor = mergeScalar(EventModelRole.Actor, Actor, other.Actor, x => x.ToString());

        // jasperfx#1298. External events follow the consumed events they flag, as tags follow emitted ones.
        var externalEvents = mergeExternalEvents();

        IReadOnlyList<ExternalEventDescriptor> mergeExternalEvents()
        {
            if (ExternalEvents.Count == 0 && other.ExternalEvents.Count == 0) return Array.Empty<ExternalEventDescriptor>();

            var tookTheirs = theirsWins(EventModelRole.ConsumedEvents);
            var winning = tookTheirs ? other.ExternalEvents : ExternalEvents;
            var losing = tookTheirs ? ExternalEvents : other.ExternalEvents;

            if (Claims(EventModelRole.ExternalEvents) || other.Claims(EventModelRole.ExternalEvents))
            {
                claimedBy[EventModelRole.ExternalEvents] = (Claims(EventModelRole.ExternalEvents) && other.Claims(EventModelRole.ExternalEvents)
                    ? (tookTheirs ? other.ProvenanceFor(EventModelRole.ConsumedEvents) : ProvenanceFor(EventModelRole.ConsumedEvents))
                    : Claims(EventModelRole.ExternalEvents) ? ProvenanceFor(EventModelRole.ExternalEvents) : other.ProvenanceFor(EventModelRole.ExternalEvents))
                    ?? EventModelProvenance.Declared;
            }

            var merged = winning.ToList();
            foreach (var external in losing)
            {
                var existing = merged.FirstOrDefault(x => SameType(x.Event, external.Event));
                if (existing is null)
                {
                    merged.Add(external);
                }
                else if (!string.Equals(existing.ExternalSystem, external.ExternalSystem, StringComparison.Ordinal)
                         && Claims(EventModelRole.ConsumedEvents) && other.Claims(EventModelRole.ConsumedEvents))
                {
                    var mine = tookTheirs ? external : existing;
                    var theirs = tookTheirs ? existing : external;
                    disagree(EventModelRole.ExternalEvents, tookTheirs,
                        $"{mine.Event.Name} from {mine.ExternalSystem}", $"{theirs.Event.Name} from {theirs.ExternalSystem}");
                }
            }

            return merged.Where(x => consumedEvents.Any(e => SameType(e, x.Event))).ToList();
        }

        var schedule = mergeScalar(EventModelRole.Schedule, Schedule, other.Schedule, x => x.ToString());
        var screen = mergeScalar(EventModelRole.Screen, Screen, other.Screen, x => x);

        // jasperfx#1298. Descriptions are prose, and two of them are two drafts of one paragraph rather than two
        // claims about the system: the richer text wins, element by element, and nothing is ever recorded.
        var (description, elementDescriptions) = mergeDescriptions();

        (string?, IReadOnlyList<ElementDescription>) mergeDescriptions()
        {
            if (!Claims(EventModelRole.Description) && !other.Claims(EventModelRole.Description))
            {
                return (null, Array.Empty<ElementDescription>());
            }

            var mineRicher = richer(Description, other.Description) == Description && Description is not null;
            claimedBy[EventModelRole.Description] = (Claims(EventModelRole.Description) && other.Claims(EventModelRole.Description)
                ? (mineRicher ? ProvenanceFor(EventModelRole.Description) : other.ProvenanceFor(EventModelRole.Description))
                : Claims(EventModelRole.Description) ? ProvenanceFor(EventModelRole.Description) : other.ProvenanceFor(EventModelRole.Description))
                ?? EventModelProvenance.Declared;

            var elements = ElementDescriptions.ToList();
            foreach (var theirs in other.ElementDescriptions)
            {
                var index = elements.FindIndex(x => SameType(x.Type, theirs.Type));
                if (index < 0)
                {
                    elements.Add(theirs);
                    continue;
                }

                var kept = richer(elements[index].Text, theirs.Text)!;
                elements[index] = elements[index] with { Text = kept };
            }

            return (richer(Description, other.Description), elements);
        }

        // jasperfx#836. Origin merges as any other scalar does, which gives the store dimension the
        // one thing it was missing: two sources that contributed the SAME slice from DIFFERENT stores
        // now leave a SourceDisagreement naming both stores, instead of the survivor carrying nothing
        // to say where it came from. A declared slice claims no origin, so it never takes one away.
        var origin = mergeOrigin();

        // jasperfx#959. A declared slice's Origin is attribution -- which model file or definition said
        // it -- not a claim about where the slice lives. Declarations always meet code from another
        // source, and spec-first work routinely has two declared sources (a model and its specs), so a
        // Declared-rung origin never disagrees: the higher rung keeps its origin, a tie keeps the first,
        // and nothing is recorded. Two stores on Derived / Observed still disagree exactly as #836 says.
        //
        // jasperfx#1083. An Observed origin is attribution in the same sense -- which service the slice
        // was watched on (CritterWatch stamps critterwatch://observed/{service}) -- and never a store. So
        // when exactly one side is Observed, runtime observation is confirming the slice rather than
        // contradicting its origin: the higher rung keeps its origin and nothing is recorded. Two Observed
        // services, like two Derived stores, still disagree.
        Uri? mergeOrigin()
        {
            var mine = ProvenanceFor(EventModelRole.Origin);
            var theirs = other.ProvenanceFor(EventModelRole.Origin);

            var attributionOnly = mine == EventModelProvenance.Declared
                                  || theirs == EventModelProvenance.Declared
                                  || (mine == EventModelProvenance.Observed) != (theirs == EventModelProvenance.Observed);

            if (attributionOnly)
            {
                return takeOther(EventModelRole.Origin) ? other.Origin : Origin;
            }

            return mergeScalar(EventModelRole.Origin, Origin, other.Origin, x => x.OriginalString);
        }

        // Hotspots are annotations rather than claims about the system, so they always union: a
        // higher rung replacing the list would throw away the findings recorded here.
        takeOther(EventModelRole.Hotspots);
        var hotspots = union(union(Hotspots, other.Hotspots, hotspotKey), disagreements, hotspotKey);

        var additionalEntryPoints = union(AdditionalEntryPoints, other.AdditionalEntryPoints, entryPointKey);
        if (foldsEntryPoints)
        {
            var handler = TriggerKind == EventModeling.TriggerKind.MessageHandler ? this : other;
            additionalEntryPoints = union(additionalEntryPoints,
                [new EventModelEntryPoint(EventModeling.TriggerKind.MessageHandler, handler.HandlerType, handler.HandlerMethod, handler.Origin)],
                entryPointKey);
        }

        return new EventModelSliceDescriptor(Name, triggerLabel, triggerType, commandType, handlerType,
            emittedEvents, projectionTypes, readModelTypes)
        {
            Pattern = pattern,
            TriggerKind = triggerKind,
            TriggeredBy = triggeredBy,
            TriggerOrigin = triggerOrigin,
            HandlerMethod = handlerMethod,
            AdditionalEntryPoints = additionalEntryPoints,
            AggregateTypes = aggregateTypes,
            PublishedMessages = publishedMessages,
            ExternalSystems = externalSystems,
            Hotspots = hotspots,
            Specifications = specifications,
            Domain = domain,
            ConsumedEvents = consumedEvents,
            ReadsFrom = readsFrom,
            Chapter = chapter,
            StartsStream = startsStream,
            EmittedEventStreams = emittedEventStreams,
            AggregateDeclaration = aggregateDeclaration,
            DeciderModel = deciderModel,
            ViewProjection = viewProjection,
            EventTags = eventTags,
            Reads = reads,
            UntaggedProjections = untaggedProjections,
            RejectionReasons = rejectionReasons,
            Actor = actor,
            ExternalEvents = externalEvents,
            Schedule = schedule,
            Screen = screen,
            Description = description,
            ElementDescriptions = elementDescriptions,
            Origin = origin,
            Provenance = higher(Provenance, other.Provenance),
            ClaimedBy = claimedBy,
        };
    }

    /// <summary>The longer of two texts, or whichever is set. What a description merge keeps (jasperfx#1298).</summary>
    private static string? richer(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return second!.Length > first!.Length ? second : first;
    }

    private static string hotspotKey(HotspotDescriptor hotspot) => $"{hotspot.Origin}:{hotspot.Text}";

    private static string entryPointKey(EventModelEntryPoint entryPoint)
        => $"{entryPoint.Kind}:{entryPoint.HandlerType?.FullName}:{entryPoint.HandlerMethod}";

    // jasperfx#954. A collision is the SAME source on the SAME rung naming two different handlers.
    // Across sources the same name with different handlers is the designed fold -- Wolverine.HTTP names
    // an endpoint's slice for its request type precisely so the endpoint and the message handler for
    // that command become one slice. The per-role merge records anything else of that shape as a
    // disagreement, and foldsEntryPointsWith() below is the one exception.
    // "The same source" has to be PROVEN by a stamped Origin: two unattributed slices may well be two
    // sources, and those keep the per-role merge and its disagreement hotspot.
    private bool collidesWith(EventModelSliceDescriptor other)
        => HandlerType is not null
           && other.HandlerType is not null
           && Origin is not null
           && Origin == other.Origin
           && ProvenanceFor(EventModelRole.HandlerType) == other.ProvenanceFor(EventModelRole.HandlerType)
           && !SameType(HandlerType, other.HandlerType);

    // jasperfx#1099. Both claims are true, nothing is wrong with either source and there is nothing for a
    // reader to go and fix, so the fold above must not read as a disagreement -- and in a model with
    // several such commands the disagreement count stopped meaning anything.
    //
    // Narrower than SplitsWhoFromHow on purpose. Only code can say a second entry point exists, so both
    // kinds must sit on Derived: a declared Http met by a message handler is an endpoint the board wants
    // and the code lacks, an observed kind outranks the code as usual, and unstamped sources read as
    // Declared and merge exactly as they always have. And it takes two sources: one source saying both
    // has contradicted itself.
    private bool foldsEntryPointsWith(EventModelSliceDescriptor other)
        => TriggerKind is { } mine
           && other.TriggerKind is { } theirs
           && TriggerKindCompatibility.AreHttpAndMessageHandler(mine, theirs)
           && ProvenanceFor(EventModelRole.TriggerKind) == EventModelProvenance.Derived
           && other.ProvenanceFor(EventModelRole.TriggerKind) == EventModelProvenance.Derived
           && (Origin is null || Origin != other.Origin);

    /// <summary>
    /// Whether two type descriptors name the same type: by <see cref="TypeDescriptor.FullName"/> when
    /// both are real types, by <see cref="TypeDescriptor.Name"/> when either is a declaration (an empty
    /// <see cref="TypeDescriptor.AssemblyName"/>, jasperfx#798). Ordinal.
    /// </summary>
    public static bool SameType(TypeDescriptor a, TypeDescriptor b)
        => string.IsNullOrEmpty(a.AssemblyName) || string.IsNullOrEmpty(b.AssemblyName)
            ? string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            : string.Equals(a.FullName, b.FullName, StringComparison.Ordinal);

    private static string render<T>(IReadOnlyList<T> items, Func<T, string> display)
        => string.Join(", ", items.Select(display));

    private static bool sameSet<T>(IReadOnlyList<T> first, IReadOnlyList<T> second, Func<T, string> key)
    {
        if (first.Count != second.Count) return false;

        var keys = new HashSet<string>(first.Select(key), StringComparer.Ordinal);
        return second.All(x => keys.Contains(key(x)));
    }

    /// <summary>The higher of two rungs, or whichever is set, or null when neither is.</summary>
    private static EventModelProvenance? higher(EventModelProvenance? first, EventModelProvenance? second)
        => (first, second) switch
        {
            (null, null) => null,
            (null, not null) => second,
            (not null, null) => first,
            _ => second!.Value.Outranks(first!.Value) ? second : first,
        };

    private static IReadOnlyList<T> union<T>(IReadOnlyList<T> first, IReadOnlyList<T> second, Func<T, string> key)
    {
        if (second.Count == 0) return first;
        if (first.Count == 0) return second;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<T>(first.Count + second.Count);
        foreach (var item in first.Concat(second))
        {
            if (seen.Add(key(item))) list.Add(item);
        }

        return list;
    }

    private (IReadOnlyList<EventModelElement> elements, IReadOnlyList<EventModelEdge> edges) buildGraph()
    {
        var elements = new List<EventModelElement>();
        var edges = new List<EventModelEdge>();

        EventModelElement add(EventModelElement element, EventModelRole? role = null)
        {
            // The rendering contract carries the ladder too (jasperfx#703), so a viewer can shade an
            // observed role differently from a declared one without re-deriving anything. Trigger
            // elements pass their role in explicitly -- they are the one kind with three possible
            // sources.
            role ??= EventModelElement.RoleFor(element.Kind);

            var stamped = role is { } claimed && ProvenanceFor(claimed) is { } rung
                ? element with { Provenance = rung }
                : element;

            // jasperfx#1298. A description rides on the element it describes, so a viewer shows it on hover
            // without a join back to the slice.
            if (stamped.Type is { } type && DescriptionOf(type) is { } described)
            {
                stamped = stamped with { Description = described };
            }

            elements.Add(stamped);
            return stamped;
        }

        void link(EventModelElement? from, EventModelElement? to)
        {
            if (from is null || to is null) return;
            edges.Add(new EventModelEdge(from.Id, to.Id));
        }

        // Wireframe lane: the trigger, inbound external systems, hotspots
        EventModelElement? trigger = null;
        if (TriggerType is not null)
        {
            trigger = add(EventModelElement.ForType(Name, EventModelElementKind.Trigger, TriggerType),
                EventModelRole.TriggerType);
        }
        else if (TriggerLabel is not null)
        {
            trigger = add(EventModelElement.ForLabel(Name, EventModelElementKind.Trigger, TriggerLabel),
                EventModelRole.TriggerLabel);
        }
        else if (TriggerOrigin?.Label is not null)
        {
            trigger = add(EventModelElement.ForLabel(Name, EventModelElementKind.Trigger, TriggerOrigin.Label),
                EventModelRole.TriggerOrigin);
        }

        // jasperfx#1298. The named screen the trigger acts through: a wireframe card by name only, beside the
        // trigger and pointing at it, since the screen is where the action happens.
        var screen = Screen is null ? null : add(EventModelElement.ForLabel(Name, EventModelElementKind.Screen, Screen));

        var inboundSystems = ExternalSystems
            .Where(x => x.Direction == ExternalSystemDirection.Inbound)
            .Select(x => add(EventModelElement.ForLabel(Name, EventModelElementKind.ExternalSystem, x.Name)))
            .ToList();

        foreach (var hotspot in Hotspots)
        {
            add(EventModelElement.ForLabel(Name, EventModelElementKind.Hotspot, hotspot.Text));
        }

        // jasperfx#1003. A pattern violation renders like any other hotspot, attributed to the rung that
        // claimed the offending role. Skipped when a stored hotspot already carries the same text, so a
        // descriptor that was round-tripped through a tool which copied it into Hotspots draws one sticky.
        foreach (var violation in PatternViolations)
        {
            if (Hotspots.Any(x => string.Equals(x.Text, violation.Text, StringComparison.Ordinal))) continue;

            add(EventModelElement.ForLabel(Name, EventModelElementKind.Hotspot, violation.Text), violation.Role);
        }

        // Command lane: command, handler, aggregates
        var command = CommandType is null ? null : add(EventModelElement.ForType(Name, EventModelElementKind.Command, CommandType));
        var handler = HandlerType is null ? null : add(EventModelElement.ForType(Name, EventModelElementKind.Handler, HandlerType));
        var aggregates = AggregateTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Aggregate, x))).ToList();

        // Event stream lane: emitted events, published messages
        var events = EmittedEvents.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Event, x))).ToList();
        var messages = PublishedMessages.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Message, x))).ToList();

        // jasperfx#824. A consumed event is the same sticky repeated where it is read, so an event a
        // slice both emits and consumes is ONE element -- the ids collide by construction, and adding
        // it twice would put two overlapping stickies on the canvas and double every edge off it.
        // Consumed events feed the projection / read model edges below exactly as emitted ones do;
        // what they do NOT get is an edge from the processor, because the slice did not write them.
        //
        // jasperfx#958. "The same event" is a type identity, not an id: a consumed event declared by name
        // ("AppointmentConfirmed", no assembly) is the emitted CritterCrush.Scheduling.AppointmentConfirmed
        // once the code exists, and comparing ids (built from FullName) drew both.
        var consumed = ConsumedEvents
            .Where(x => EmittedEvents.All(e => !SameType(e, x)))
            .Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Event, x), EventModelRole.ConsumedEvents))
            .ToList();

        var inboundEvents = events.Concat(consumed).ToList();

        // Read model lane: projections, read models
        var projections = ProjectionTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Projection, x))).ToList();
        var readModels = ReadModelTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.ReadModel, x))).ToList();

        // Same dedupe rule as consumed events, for the same reason: a slice that both reads and
        // produces one read model draws one sticky.
        var readsFrom = ReadsFrom
            .Where(x => ReadModelTypes.All(r => !SameType(r, x)))
            .Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.ReadModel, x), EventModelRole.ReadsFrom))
            .ToList();

        var outboundSystems = ExternalSystems
            .Where(x => x.Direction == ExternalSystemDirection.Outbound)
            .Select(x => add(EventModelElement.ForLabel(Name, EventModelElementKind.ExternalSystem, x.Name)))
            .ToList();

        // Edges. The "processor" is the handler when there is one, else the command.
        var entry = command ?? handler;
        var processor = handler ?? command;

        link(screen, trigger ?? entry);
        link(trigger, entry);
        foreach (var system in inboundSystems) link(system, entry);
        link(command, handler);
        foreach (var aggregate in aggregates) link(processor, aggregate);
        foreach (var evt in events) link(processor, evt);
        foreach (var message in messages) link(processor, message);
        foreach (var message in messages)
        foreach (var system in outboundSystems)
        {
            link(message, system);
        }

        if (messages.Count == 0)
        {
            foreach (var evt in events)
            foreach (var system in outboundSystems)
            {
                link(evt, system);
            }
        }

        if (projections.Count > 0)
        {
            foreach (var evt in inboundEvents)
            foreach (var projection in projections)
            {
                link(evt, projection);
            }

            foreach (var projection in projections)
            foreach (var readModel in readModels)
            {
                link(projection, readModel);
            }
        }
        else
        {
            foreach (var evt in inboundEvents)
            foreach (var readModel in readModels)
            {
                link(evt, readModel);
            }
        }

        // jasperfx#824. The Automation pattern's input edge: a read model consulted before deciding
        // points AT the processor, the opposite direction from one the slice produces. When there is
        // no processor at all this is a UI read, so it points at the trigger instead.
        foreach (var read in readsFrom)
        {
            link(read, entry ?? trigger);
        }

        // A view slice with no events of its own reads straight from its read models
        if (inboundEvents.Count == 0 && processor is null && trigger is not null)
        {
            foreach (var readModel in readModels) link(readModel, trigger);
        }

        return (elements, edges);
    }
}

/// <summary>
/// Wire descriptor for an entire Event Model — every slice, plus the model-level
/// aggregate elements the slices reference by type.
/// </summary>
/// <remarks>
/// The positional constructor is the original 2.x shape and is kept source- and
/// binary-compatible; <see cref="Aggregates"/> and <see cref="Hotspots"/> are additive
/// <c>init</c> properties. Use <see cref="Merge"/> to assemble the full picture from several
/// sources.
/// </remarks>
/// <param name="Name">Display name of the model.</param>
/// <param name="Slices">Slices that make up the model, in declaration order.</param>
public sealed record EventModelDescriptor(
    string Name,
    IReadOnlyList<EventModelSliceDescriptor> Slices)
{
    /// <summary>Slices that make up the model, in declaration order. Never null (jasperfx#807).</summary>
    public IReadOnlyList<EventModelSliceDescriptor> Slices { get; init; }
        = Slices ?? Array.Empty<EventModelSliceDescriptor>();

    /// <summary>
    /// The aggregate elements of the model — one per aggregate-shaped CLR type, with its kind and
    /// applied events. Slices point at these through
    /// <see cref="EventModelSliceDescriptor.AggregateTypes"/>.
    /// </summary>
    public IReadOnlyList<AggregateDescriptor> Aggregates { get; init; } = Array.Empty<AggregateDescriptor>();

    /// <summary>
    /// Declared policies putting handlers and endpoints in domains — modules, in a modular monolith
    /// (jasperfx#960). Applied by <c>EventModelDomains.Resolve</c> in the sources that derive slices
    /// from code. Never null.
    /// </summary>
    public IReadOnlyList<DomainAssignmentDescriptor> DomainAssignments { get; init; } = Array.Empty<DomainAssignmentDescriptor>();

    /// <summary>
    /// Hotspots that belong to the model rather than to any one slice — the open question that
    /// spans the whole flow, declared through the overlay with <c>Hotspot("…")</c> on the
    /// <c>EventModelBuilder</c> (jasperfx#690). Hotspots about a single slice live on
    /// <see cref="EventModelSliceDescriptor.Hotspots"/> instead, where they render in that slice's
    /// wireframe lane.
    /// </summary>
    public IReadOnlyList<HotspotDescriptor> Hotspots { get; init; } = Array.Empty<HotspotDescriptor>();

    /// <summary>
    /// The model's tag catalogue — every consistency tag a Dynamic Consistency Boundary can select events by,
    /// with its strong type and inner primitive where known (jasperfx#1297). Mirrors <see cref="Aggregates"/>:
    /// slices point at these by name through <see cref="EventModelSliceDescriptor.EventTags"/> and
    /// <see cref="EventModelSliceDescriptor.Reads"/>. Never null.
    /// </summary>
    public IReadOnlyList<EventTagDescriptor> Tags { get; init; } = Array.Empty<EventTagDescriptor>();

    /// <summary>
    /// The fields of the model's types — commands, events, read models, aggregates — one shape per type
    /// (jasperfx#1299). Model level because one type appears in many slices. Never null.
    /// </summary>
    /// <remarks>
    /// Derived by the source generator into the event-model manifest, or by reflection at describe time;
    /// declared with <c>EventModelBuilder.Shape&lt;T&gt;()</c> for stub-first work. <see cref="Merge"/> keeps the
    /// higher rung's fields per type, and a declared field the code lacks becomes a model-level
    /// <see cref="HotspotOrigin.SourceDisagreement"/> on <see cref="EventModelRole.Fields"/>.
    /// </remarks>
    public IReadOnlyList<TypeShapeDescriptor> Shapes { get; init; } = Array.Empty<TypeShapeDescriptor>();

    /// <summary>The shape recorded for <paramref name="type"/>, or null when the model carries none (jasperfx#1299).</summary>
    public TypeShapeDescriptor? ShapeOf(TypeDescriptor type)
        => Shapes.FirstOrDefault(x => EventModelSliceDescriptor.SameType(x.Type, type));

    /// <summary>The catalogue entry named <paramref name="tag"/>, or null (jasperfx#1297).</summary>
    public EventTagDescriptor? TagNamed(string tag)
        => Tags.FirstOrDefault(x => string.Equals(x.Name, tag, StringComparison.Ordinal));

    /// <summary>
    /// The consistency boundary <paramref name="slice"/>'s reads imply, resolved against the whole model
    /// (jasperfx#1297): each term carries the emitted events the slice tags with the term's tag <em>and</em> the
    /// events the read model folds — an aggregate's or decider model's <see cref="AggregateDescriptor.AppliedEvents"/>
    /// for a model read, the producing View slice's <see cref="EventModelSliceDescriptor.ConsumedEvents"/> for a
    /// projection read. The dcb.events per-command table: read, event types folded, tags, resulting query.
    /// </summary>
    public BoundaryQueryDescriptor BoundaryQueryFor(EventModelSliceDescriptor slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var local = slice.BoundaryQuery;
        if (local.IsEmpty) return local;

        var terms = new List<BoundaryQueryTerm>();
        foreach (var term in local.Terms)
        {
            var types = term.EventTypes.ToList();

            foreach (var read in slice.Reads.Where(x => string.Equals(x.Tag, term.Tag, StringComparison.Ordinal)))
            {
                foreach (var folded in FoldedEventsOf(read)) addType(types, folded);
            }

            terms.Add(term with { EventTypes = types });
        }

        return new BoundaryQueryDescriptor(terms);

        static void addType(List<TypeDescriptor> list, TypeDescriptor type)
        {
            if (!list.Any(x => EventModelSliceDescriptor.SameType(x, type))) list.Add(type);
        }
    }

    /// <summary>
    /// The events the thing a boundary read reads is folded from, as the model knows them (jasperfx#1297): an
    /// aggregate's applied events for a <see cref="BoundaryReadKind.Model"/> read, the consumed events of the View
    /// slices producing the read model (or the projection's own consumed events) for a
    /// <see cref="BoundaryReadKind.Projection"/> read. Empty when the model cannot say.
    /// </summary>
    public IReadOnlyList<TypeDescriptor> FoldedEventsOf(BoundaryReadDescriptor read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var found = new List<TypeDescriptor>();

        void take(IEnumerable<TypeDescriptor> types)
        {
            foreach (var type in types)
            {
                if (!found.Any(x => EventModelSliceDescriptor.SameType(x, type))) found.Add(type);
            }
        }

        foreach (var aggregate in Aggregates.Where(x => EventModelSliceDescriptor.SameType(x.Type, read.Type)))
        {
            take(aggregate.AppliedEvents);
        }

        foreach (var slice in Slices)
        {
            var produces = slice.ReadModelTypes.Any(x => EventModelSliceDescriptor.SameType(x, read.Type))
                           || slice.ProjectionTypes.Any(x => EventModelSliceDescriptor.SameType(x, read.Type));
            if (produces) take(slice.ConsumedEvents);
        }

        return found;
    }

    /// <summary>
    /// Fields whose value comes from nowhere the model can see, as <see cref="HotspotOrigin.IncompleteInformation"/>
    /// hotspots graded as questions (jasperfx#1299). Computed from the merged slices and <see cref="Shapes"/> on
    /// every read, like <see cref="EventModelSliceDescriptor.PatternViolations"/>, so a deserializer ignores it and
    /// recomputes. Each hotspot names its slice on <see cref="HotspotDescriptor.SliceName"/> and its field on
    /// <see cref="HotspotDescriptor.Field"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two questions, one per direction of a slice. A <see cref="SlicePattern.View"/>'s read-model field that matches
    /// no consumed event's field by name (case-insensitive) or by <see cref="FieldDescriptor.Mapping"/>: where does
    /// the view get it? An emitted event's field that comes from neither the command, an aggregate or decider model
    /// the slice decides against, nor a <c>[Generated]</c> value: where does the event get it?
    /// </para>
    /// <para>
    /// Raised only for a slice with a <see cref="EventModelProvenance.Declared"/> or
    /// <see cref="EventModelProvenance.Specified"/> rung, and only for types whose shape the model carries. A purely
    /// derived model gets none: the code is the answer there, and the question belongs to design-first work.
    /// </para>
    /// </remarks>
    public IReadOnlyList<HotspotDescriptor> IncompleteInformation
        => Slices.SelectMany(IncompleteInformationFor).ToList();

    /// <summary>The <see cref="IncompleteInformation"/> questions about one slice (jasperfx#1299).</summary>
    public IReadOnlyList<HotspotDescriptor> IncompleteInformationFor(EventModelSliceDescriptor slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (!slice.HasRung(EventModelProvenance.Declared) && !slice.HasRung(EventModelProvenance.Specified))
        {
            return Array.Empty<HotspotDescriptor>();
        }

        var questions = new List<HotspotDescriptor>();

        if (slice.Pattern == SlicePattern.View)
        {
            var sources = slice.ConsumedEvents;
            foreach (var readModel in slice.ReadModelTypes)
            {
                var shape = ShapeOf(readModel);
                if (shape is null) continue;

                foreach (var field in shape.Fields)
                {
                    if (field.Generated || field.Identity) continue;
                    if (comesFrom(field, sources)) continue;

                    questions.Add(HotspotDescriptor.IncompleteInformation(slice.Name, readModel.Name, field.Name,
                        $"{readModel.Name}.{field.Name} comes from none of the events the view consumes" +
                        (sources.Count == 0 ? "" : $" ({string.Join(", ", sources.Select(x => x.Name))})") +
                        ". Map it with Field(\"" + field.Name + "\").From<…>(…), or consume the event that carries it"));
                }
            }
        }
        else if (slice.Pattern is SlicePattern.Command or SlicePattern.Automation or SlicePattern.Translation or null)
        {
            var sources = new List<TypeDescriptor>();
            if (slice.CommandType is { } command) sources.Add(command);
            sources.AddRange(slice.AggregateTypes);
            if (slice.DeciderModel is { } decider && !sources.Any(x => EventModelSliceDescriptor.SameType(x, decider))) sources.Add(decider);
            sources.AddRange(slice.ConsumedEvents.Where(c => !sources.Any(x => EventModelSliceDescriptor.SameType(x, c))));
            if (sources.Count == 0) return questions;

            foreach (var emitted in slice.EmittedEvents)
            {
                var shape = ShapeOf(emitted);
                if (shape is null) continue;

                foreach (var field in shape.Fields)
                {
                    if (field.Generated) continue;
                    if (comesFrom(field, sources)) continue;

                    questions.Add(HotspotDescriptor.IncompleteInformation(slice.Name, emitted.Name, field.Name,
                        $"{emitted.Name}.{field.Name} comes from neither the command, the aggregate nor a generated value" +
                        $" ({string.Join(", ", sources.Select(x => x.Name))}). Map it with Field(\"{field.Name}\").From<…>(…), mark it [Generated], or add it to the command"));
                }
            }
        }

        return questions;
    }

    /// <summary>
    /// Whether <paramref name="field"/> is supplied by one of <paramref name="sources"/>: by its mapping, when the
    /// mapping names one of them; else by a field of the same name (case-insensitive) on a source whose shape the
    /// model carries. A source with no shape cannot vouch for a field, except through a mapping.
    /// </summary>
    private bool comesFrom(FieldDescriptor field, IReadOnlyList<TypeDescriptor> sources)
    {
        if (field.Mapping is { } mapping)
        {
            foreach (var source in sources)
            {
                if (!EventModelSliceDescriptor.SameType(mapping.Source, source)) continue;

                var sourceShape = ShapeOf(source);
                if (sourceShape is null || sourceShape.Find(mapping.Field) is not null) return true;
            }
        }

        foreach (var source in sources)
        {
            if (ShapeOf(source)?.Find(field.Name) is not null) return true;
        }

        return false;
    }

    /// <summary>
    /// The cross-slice cause→effect relationships, by element id — computed from the roles every
    /// slice already stamps, on every read (jasperfx#823).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never accepted as input, exactly as <see cref="EventModelSliceDescriptor.Elements"/> and
    /// <see cref="EventModelSliceDescriptor.Edges"/> are not: a deserializer reading a document that
    /// carries <c>links</c> ignores them and recomputes, so the wire can carry the rendering contract
    /// without the two ever disagreeing. <see cref="Merge"/> therefore has nothing to merge here —
    /// links are recomputed over the merged slices.
    /// </para>
    /// <para>
    /// The join itself is <see cref="EventModelLinks.Compute"/>, public and pure so Wolverine's
    /// Automation reclassification can be based on it rather than on a private copy of the same rule.
    /// </para>
    /// </remarks>
    public IReadOnlyList<EventModelLink> Links => EventModelLinks.Compute(this);

    /// <summary>
    /// Stamp every unattributed slice with <paramref name="provenance"/> — the rung of the source
    /// that produced this descriptor (jasperfx#703). Slices a source attributed itself are left alone.
    /// </summary>
    /// <remarks>
    /// Unattributed aggregates are stamped too (jasperfx#1004), so the merge can rank two sources' claims on
    /// an aggregate's <see cref="AggregateDescriptor.IdentityType"/>.
    /// </remarks>
    public EventModelDescriptor WithProvenance(EventModelProvenance provenance)
        => this with
        {
            Slices = Slices.Select(x => x.WithProvenance(provenance)).ToList(),
            Aggregates = Aggregates.Select(x => x.Provenance is null ? x with { Provenance = provenance } : x).ToList(),
            // jasperfx#1297, jasperfx#1299: the tag catalogue and the shapes rank their claims the same way.
            Tags = Tags.Select(x => x.Provenance is null ? x with { Provenance = provenance } : x).ToList(),
            Shapes = Shapes.Select(x => x.Provenance is null ? x with { Provenance = provenance } : x).ToList(),
        };

    /// <summary>
    /// Assemble one model from several sources' descriptors. Slices with the same name are folded
    /// with <see cref="EventModelSliceDescriptor.Merge"/>, which decides each role by the
    /// <see cref="EventModelProvenance"/> ladder — observed beats derived beats declared — and falls
    /// back to the order the descriptors are given only to break a tie between sources on the same
    /// rung. Aggregates are unioned by type and model-level hotspots by origin + text. Slice order is
    /// first appearance.
    /// </summary>
    /// <remarks>
    /// ⚠️ Before jasperfx#703 this was first-wins for every role, and callers registered derived
    /// sources ahead of overlays to make derived roles beat declared ones. Registration order is no
    /// longer the mechanism: stamp the sources instead, through
    /// <see cref="IEventModelDefinitionSource.Provenance"/> or
    /// <see cref="EventModelSliceDescriptor.WithProvenance"/>. Unstamped sources all sit on
    /// <see cref="EventModelProvenance.Declared"/>, tie, and merge exactly as they did before.
    /// </remarks>
    /// <param name="name">Name of the assembled model.</param>
    /// <param name="descriptors">Descriptors to fold. Order breaks ties within a rung.</param>
    public static EventModelDescriptor Merge(string name, IEnumerable<EventModelDescriptor> descriptors)
    {
        var slices = new List<EventModelSliceDescriptor>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var aggregates = new List<AggregateDescriptor>();
        var hotspots = new List<HotspotDescriptor>();
        var hotspotKeys = new HashSet<string>(StringComparer.Ordinal);
        var assignments = new List<DomainAssignmentDescriptor>();
        var declaredAggregates = new List<TypeDescriptor>();
        var tags = new List<EventTagDescriptor>();
        var shapes = new List<TypeShapeDescriptor>();

        void record(HotspotDescriptor hotspot)
        {
            if (hotspotKeys.Add($"{hotspot.Origin}:{hotspot.Text}")) hotspots.Add(hotspot);
        }

        foreach (var descriptor in descriptors)
        {
            assignments.AddRange(descriptor.DomainAssignments);

            // jasperfx#1297. The catalogue unions by name. A real tag type replaces a declaration, and two
            // different types for one tag are ranked like an aggregate's identity: the higher rung wins and the
            // dropped claim is a model-level hotspot on TagType.
            foreach (var tag in descriptor.Tags)
            {
                var existing = tags.FindIndex(x => string.Equals(x.Name, tag.Name, StringComparison.Ordinal));
                if (existing < 0)
                {
                    tags.Add(tag);
                    continue;
                }

                tags[existing] = mergeTag(tags[existing], tag, record);
            }

            // jasperfx#1299. Shapes union by type; TypeShapeDescriptor.Merge holds the field rule.
            foreach (var shape in descriptor.Shapes)
            {
                var existing = shapes.FindIndex(x => EventModelSliceDescriptor.SameType(x.Type, shape.Type));
                if (existing < 0)
                {
                    shapes.Add(shape);
                    continue;
                }

                shapes[existing] = shapes[existing].Merge(shape, record);
            }

            // jasperfx#1117. Only aggregates a person declared count: the store derives one for every
            // single-stream projection, which would make every View look like a write model's fold.
            declaredAggregates.AddRange(descriptor.Aggregates
                .Where(x => (x.Provenance ?? EventModelProvenance.Declared) == EventModelProvenance.Declared)
                .Select(x => x.Type));

            foreach (var slice in descriptor.Slices)
            {
                if (indexByName.TryGetValue(slice.Name, out var index))
                {
                    slices[index] = slices[index].Merge(slice);
                }
                else
                {
                    indexByName[slice.Name] = slices.Count;
                    slices.Add(slice);
                }
            }

            // jasperfx#958. One aggregate per TYPE IDENTITY, not per FullName: an aggregate declared by
            // name ("Appointment", no assembly) is the same aggregate as the real type once it exists.
            // First wins as before, except that a real type replaces a declaration in place -- it
            // carries the namespace and the applied events the declaration could not know.
            foreach (var aggregate in descriptor.Aggregates)
            {
                var existing = aggregates.FindIndex(x => EventModelSliceDescriptor.SameType(x.Type, aggregate.Type));
                if (existing < 0)
                {
                    aggregates.Add(aggregate);
                    continue;
                }

                var kept = aggregates[existing];
                var entry = string.IsNullOrEmpty(kept.Type.AssemblyName) && !string.IsNullOrEmpty(aggregate.Type.AssemblyName)
                    ? aggregate
                    : kept;

                // jasperfx#1004. The identity type is a claim, ranked like a slice role: the higher rung wins,
                // a tie keeps the first, and a dropped claim becomes a model-level hotspot.
                aggregates[existing] = mergeIdentity(entry, kept, aggregate, hotspot =>
                {
                    if (hotspotKeys.Add($"{hotspot.Origin}:{hotspot.Text}")) hotspots.Add(hotspot);
                });
            }

            foreach (var hotspot in descriptor.Hotspots)
            {
                if (hotspotKeys.Add($"{hotspot.Origin}:{hotspot.Text}")) hotspots.Add(hotspot);
            }
        }

        // jasperfx#960. Policies are declarations, not claims: they union. Conflicting ones are reported
        // when they are applied (EventModelDomains.Resolve), where the type they disagree about is known.
        var domainAssignments = assignments.Distinct().ToList();

        placeSnapshotsOfDeclaredAggregates(slices, declaredAggregates);

        return new EventModelDescriptor(name, slices)
        {
            Aggregates = aggregates, Hotspots = hotspots, DomainAssignments = domainAssignments,
            Tags = tags, Shapes = shapes,
        };
    }

    /// <summary>
    /// jasperfx#1297. Two catalogue entries for one tag name: the real type replaces a declaration, the inner
    /// primitive fills in from whichever side knows it, and two different tag types are ranked by rung with the
    /// loser recorded on <see cref="EventModelRole.TagType"/>.
    /// </summary>
    private static EventTagDescriptor mergeTag(EventTagDescriptor first, EventTagDescriptor second,
        Action<HotspotDescriptor> disagreement)
    {
        var firstRung = first.Provenance ?? EventModelProvenance.Declared;
        var secondRung = second.Provenance ?? EventModelProvenance.Declared;
        var takeSecond = secondRung.Outranks(firstRung);
        var provenance = takeSecond ? second.Provenance : first.Provenance ?? second.Provenance;

        TypeDescriptor? type;
        if (first.Type is null || second.Type is null)
        {
            type = first.Type ?? second.Type;
        }
        else if (EventModelSliceDescriptor.SameType(first.Type, second.Type))
        {
            type = string.IsNullOrEmpty(first.Type.AssemblyName) ? second.Type : first.Type;
        }
        else
        {
            type = takeSecond ? second.Type : first.Type;

            var sameName = string.Equals(first.Type.Name, second.Type.Name, StringComparison.Ordinal);
            string display(TypeDescriptor t) => sameName ? t.FullName : t.Name;

            var firstClaim = new EventModelClaim(firstRung, display(first.Type));
            var secondClaim = new EventModelClaim(secondRung, display(second.Type));
            var hotspot = takeSecond
                ? HotspotDescriptor.SourceDisagreement(EventModelRole.TagType, secondClaim, firstClaim)
                : HotspotDescriptor.SourceDisagreement(EventModelRole.TagType, firstClaim, secondClaim);

            var prefix = $"{EventModelRole.TagType}:";
            disagreement(hotspot with
            {
                Text = $"{EventModelRole.TagType} of {first.Name}:{hotspot.Text.Substring(prefix.Length)}",
            });
        }

        return first with
        {
            Type = type,
            InnerType = (takeSecond ? second.InnerType : first.InnerType) ?? first.InnerType ?? second.InnerType,
            Provenance = provenance,
        };
    }

    /// <summary>
    /// jasperfx#1117. A store-derived View slice whose read model is an aggregate the declared model names
    /// (an inline snapshot, say) is that aggregate's own fold, not a separate view: it joins the domain and
    /// chapter of the declared slices that decide against the aggregate, rather than landing in
    /// "(no domain) / (no chapter)". Left alone when it already has a placement, when the declaring slices
    /// have none, or when they disagree about it.
    /// </summary>
    private static void placeSnapshotsOfDeclaredAggregates(List<EventModelSliceDescriptor> slices,
        List<TypeDescriptor> declaredAggregates)
    {
        if (declaredAggregates.Count == 0) return;

        for (var i = 0; i < slices.Count; i++)
        {
            var view = slices[i];
            if (view.Domain is not null || view.Chapter is not null) continue;
            if (view.Pattern != SlicePattern.View || view.ReadModelTypes.Count == 0) continue;
            if (view.Provenance != EventModelProvenance.Derived) continue;

            var aggregate = view.ReadModelTypes[0];
            if (!declaredAggregates.Any(x => EventModelSliceDescriptor.SameType(x, aggregate))) continue;

            var placements = slices
                .Where(x => !ReferenceEquals(x, view) && x.Chapter is not null)
                .Where(x => x.AggregateTypes.Any(t => EventModelSliceDescriptor.SameType(t, aggregate))
                            || (x.StartsStream is { } starts && EventModelSliceDescriptor.SameType(starts, aggregate)))
                .Select(x => (x.Domain, x.Chapter))
                .Distinct()
                .ToList();

            if (placements.Count != 1) continue;

            slices[i] = view with { Domain = placements[0].Domain, Chapter = placements[0].Chapter };
        }
    }

    private static AggregateDescriptor mergeIdentity(AggregateDescriptor entry, AggregateDescriptor first,
        AggregateDescriptor second, Action<HotspotDescriptor> disagreement)
    {
        var firstRung = first.Provenance ?? EventModelProvenance.Declared;
        var secondRung = second.Provenance ?? EventModelProvenance.Declared;
        var provenance = secondRung.Outranks(firstRung) ? second.Provenance : first.Provenance ?? second.Provenance;

        TypeDescriptor? identity;
        if (first.IdentityType is null || second.IdentityType is null)
        {
            identity = first.IdentityType ?? second.IdentityType;
        }
        else
        {
            var takeSecond = secondRung.Outranks(firstRung);
            identity = takeSecond ? second.IdentityType : first.IdentityType;

            if (!EventModelSliceDescriptor.SameType(first.IdentityType, second.IdentityType))
            {
                var sameName = string.Equals(first.IdentityType.Name, second.IdentityType.Name, StringComparison.Ordinal);
                string display(TypeDescriptor type) => sameName ? type.FullName : type.Name;

                var firstClaim = new EventModelClaim(firstRung, display(first.IdentityType));
                var secondClaim = new EventModelClaim(secondRung, display(second.IdentityType));

                var hotspot = takeSecond
                    ? HotspotDescriptor.SourceDisagreement(EventModelRole.AggregateIdentity, secondClaim, firstClaim)
                    : HotspotDescriptor.SourceDisagreement(EventModelRole.AggregateIdentity, firstClaim, secondClaim);

                // The role alone does not say which aggregate; a model-level hotspot has no slice to sit on
                var prefix = $"{EventModelRole.AggregateIdentity}:";
                disagreement(hotspot with
                {
                    Text = $"{EventModelRole.AggregateIdentity} of {entry.Type.Name}:{hotspot.Text.Substring(prefix.Length)}",
                });
            }
        }

        return entry with { IdentityType = identity, Provenance = provenance };
    }

    /// <summary>
    /// Fold descriptors into <em>one model per name</em>, in first-appearance order — several models
    /// in one host are legal, supported, and never an error (jasperfx#837).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart to <see cref="Merge"/>, and the one to reach for by default. <c>Merge</c>
    /// answers "fold these sources' views of <em>one</em> model"; this answers "fold these sources,
    /// whatever models they describe". Calling <c>Merge</c> across descriptors that do not name the
    /// same model is the mistake jasperfx#837 records: a modular monolith whose modules each name
    /// their own Event Model loses every name but one, and has the slices folded into whichever
    /// survived.
    /// </para>
    /// <para>
    /// <see cref="EventModelSetDescriptor"/> is the shape to carry the result on a wire that used to
    /// take a single descriptor.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<EventModelDescriptor> GroupByName(IEnumerable<EventModelDescriptor> descriptors)
    {
        var order = new List<string>();
        var byName = new Dictionary<string, List<EventModelDescriptor>>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            if (!byName.TryGetValue(descriptor.Name, out var list))
            {
                byName[descriptor.Name] = list = new List<EventModelDescriptor>();
                order.Add(descriptor.Name);
            }

            list.Add(descriptor);
        }

        return order.Select(name => Merge(name, byName[name])).ToList();
    }
}
