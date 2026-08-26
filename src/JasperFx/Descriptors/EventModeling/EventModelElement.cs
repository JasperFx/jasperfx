using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Kind of a rendered element on the Event Modeling canvas. Maps 1:1 to the
/// canonical Event Modeling / Event Storming sticky-note colour (see
/// <see cref="EventModelPalette"/>) so any viewer — the Bobcat viewer,
/// CritterWatch, a standalone page — colours the same descriptor the same way.
/// </summary>
public enum EventModelElementKind
{
    /// <summary>A UI / wireframe or other human or external trigger. White.</summary>
    Trigger,

    /// <summary>The inbound command / message type. Blue.</summary>
    Command,

    /// <summary>The handler (Wolverine handler or endpoint method) that processes the command. Blue, outlined.</summary>
    Handler,

    /// <summary>An aggregate / projected write model the handler loads or decides against. Pale yellow.</summary>
    Aggregate,

    /// <summary>An emitted domain event. Orange.</summary>
    Event,

    /// <summary>A published (non-event) message — a cascaded command or integration message. Blue, dashed.</summary>
    Message,

    /// <summary>A projection that folds events into a read model. Green, outlined.</summary>
    Projection,

    /// <summary>A read model / view. Green.</summary>
    ReadModel,

    /// <summary>An external system on either end of a translation. Pink.</summary>
    ExternalSystem,

    /// <summary>A hotspot — an open question, conflict, or pending specification. Magenta / red.</summary>
    Hotspot,
}

/// <summary>
/// Swim lane an element is rendered in. The four lanes of a standard Event
/// Modeling canvas, top to bottom (jasperfx#687 decision 9).
/// </summary>
public enum EventModelLane
{
    /// <summary>Top lane: wireframes, UI, human and external triggers, hotspots.</summary>
    Wireframe,

    /// <summary>Second lane: commands, handlers, aggregates, automations — the "timeline" of decisions.</summary>
    Command,

    /// <summary>Third lane: the event stream — emitted events and published messages.</summary>
    EventStream,

    /// <summary>Bottom lane: projections and read models.</summary>
    ReadModel,
}

/// <summary>
/// One rendered element of a slice — the rendering contract every viewer can
/// draw from without a second transform (jasperfx#687 decision 9).
/// </summary>
/// <param name="Id">
///     Stable id, unique within the slice and deterministic across runs:
///     <c>{slice name}/{kind}/{type full name or label}</c>. Edges reference elements by this id,
///     and drift colouring matches declared vs. implemented elements by the <see cref="Type"/>
///     identity underneath it.
/// </param>
/// <param name="Kind">Element kind → canonical colour.</param>
/// <param name="Lane">Swim lane the element renders in.</param>
/// <param name="Label">Display text — the CLR type's short name, or the free-form label for typeless triggers / hotspots.</param>
/// <param name="Type">The CLR type identity underneath the element, when there is one. Null for labelled triggers, external systems and hotspots.</param>
public sealed record EventModelElement(
    string Id,
    EventModelElementKind Kind,
    EventModelLane Lane,
    string Label,
    TypeDescriptor? Type)
{
    /// <summary>
    /// Compose the deterministic element id for <paramref name="kind"/> inside <paramref name="sliceName"/>.
    /// </summary>
    public static string IdFor(string sliceName, EventModelElementKind kind, string identity)
        => $"{sliceName}/{kind}/{identity}";

    /// <summary>Build the element for a CLR-typed role.</summary>
    public static EventModelElement ForType(string sliceName, EventModelElementKind kind, TypeDescriptor type)
        => new(IdFor(sliceName, kind, type.FullName), kind, LaneFor(kind), type.Name, type);

    /// <summary>Build the element for a labelled, typeless role (a trigger label, an external system, a hotspot).</summary>
    public static EventModelElement ForLabel(string sliceName, EventModelElementKind kind, string label)
        => new(IdFor(sliceName, kind, label), kind, LaneFor(kind), label, null);

    /// <summary>
    /// Which rung of the provenance ladder claimed the role this element renders (jasperfx#703) —
    /// stamped from the owning slice's <see cref="EventModelSliceDescriptor.ProvenanceFor"/>, so a
    /// viewer can shade "production has seen this happen" differently from "somebody wrote it down"
    /// without re-deriving anything.
    /// </summary>
    /// <remarks>
    /// The <em>effective</em> rung, so it follows the same one rule the merge does: an unattributed
    /// source is treated as a declaration, and its elements read
    /// <see cref="EventModelProvenance.Declared"/> rather than null. Null means only that nothing
    /// claims the role at all, which for a rendered element happens only on the kinds that have no
    /// role of their own.
    /// </remarks>
    public EventModelProvenance? Provenance { get; init; }

    /// <summary>The role an element of <paramref name="kind"/> renders, for provenance lookup.</summary>
    /// <remarks>
    /// A trigger is the one kind with three possible sources — <c>TriggerType</c>, <c>TriggerLabel</c>
    /// and <c>TriggerOrigin</c> — so the slice resolves that one itself rather than going through here.
    /// </remarks>
    public static EventModelRole? RoleFor(EventModelElementKind kind) => kind switch
    {
        EventModelElementKind.Command => EventModelRole.CommandType,
        EventModelElementKind.Handler => EventModelRole.HandlerType,
        EventModelElementKind.Aggregate => EventModelRole.AggregateTypes,
        EventModelElementKind.Event => EventModelRole.EmittedEvents,
        EventModelElementKind.Message => EventModelRole.PublishedMessages,
        EventModelElementKind.Projection => EventModelRole.ProjectionTypes,
        EventModelElementKind.ReadModel => EventModelRole.ReadModelTypes,
        EventModelElementKind.ExternalSystem => EventModelRole.ExternalSystems,
        EventModelElementKind.Hotspot => EventModelRole.Hotspots,
        _ => null,
    };

    /// <summary>The canonical lane for an element kind.</summary>
    public static EventModelLane LaneFor(EventModelElementKind kind) => kind switch
    {
        EventModelElementKind.Trigger => EventModelLane.Wireframe,
        EventModelElementKind.ExternalSystem => EventModelLane.Wireframe,
        EventModelElementKind.Hotspot => EventModelLane.Wireframe,
        EventModelElementKind.Command => EventModelLane.Command,
        EventModelElementKind.Handler => EventModelLane.Command,
        EventModelElementKind.Aggregate => EventModelLane.Command,
        EventModelElementKind.Event => EventModelLane.EventStream,
        EventModelElementKind.Message => EventModelLane.EventStream,
        EventModelElementKind.Projection => EventModelLane.ReadModel,
        EventModelElementKind.ReadModel => EventModelLane.ReadModel,
        _ => EventModelLane.Command,
    };
}

/// <summary>
/// A directed relationship between two elements of a slice, by element id.
/// </summary>
/// <param name="FromId">Id of the source element.</param>
/// <param name="ToId">Id of the target element.</param>
public sealed record EventModelEdge(string FromId, string ToId);

/// <summary>
/// The canonical Event Modeling / Event Storming sticky-note colours, keyed by
/// <see cref="EventModelElementKind"/>. Viewers are free to theme, but this is
/// the shared reference so two viewers of one descriptor agree on what a colour means.
/// </summary>
public static class EventModelPalette
{
    /// <summary>Hex colour for an element kind.</summary>
    public static string ColorFor(EventModelElementKind kind) => kind switch
    {
        EventModelElementKind.Trigger => "#FFFFFF",        // wireframe / UI — white
        EventModelElementKind.Command => "#5B9BD5",        // command — blue
        EventModelElementKind.Handler => "#5B9BD5",        // handler — blue (drawn outlined)
        EventModelElementKind.Aggregate => "#FFF2A8",      // aggregate — pale yellow
        EventModelElementKind.Event => "#F5A623",          // event — orange
        EventModelElementKind.Message => "#5B9BD5",        // message — blue (drawn dashed)
        EventModelElementKind.Projection => "#7ED321",     // projection — green (drawn outlined)
        EventModelElementKind.ReadModel => "#7ED321",      // read model — green
        EventModelElementKind.ExternalSystem => "#F8BBD0", // external system — pink
        EventModelElementKind.Hotspot => "#E91E63",        // hotspot — magenta
        _ => "#CCCCCC",
    };
}
