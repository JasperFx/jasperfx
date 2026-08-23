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
/// <param name="HandlerType">The handler / endpoint type that processes the command. Distinct from the aggregate(s).</param>
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

    /// <summary>
    /// Which of the four canonical Event Modeling patterns this slice is. Null until a source
    /// derives it.
    /// </summary>
    public SlicePattern? Pattern { get; init; }

    /// <summary>What starts the slice. Null until a source derives it.</summary>
    public TriggerKind? TriggerKind { get; init; }

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

    /// <summary>Hotspots attached to this slice — primarily pending specifications (jasperfx#689).</summary>
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
    /// Fold another source's view of the <em>same</em> slice into this one. Scalars keep the
    /// first non-null value (this instance wins); lists are unioned in order, deduplicated by
    /// identity. The overlay is typically merged <em>onto</em> the derived slice so derived roles
    /// are never overwritten by a name-only declaration.
    /// </summary>
    public EventModelSliceDescriptor Merge(EventModelSliceDescriptor other)
    {
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cannot merge slice '{other.Name}' into slice '{Name}'; slices merge by name.",
                nameof(other));
        }

        return new EventModelSliceDescriptor(
            Name,
            TriggerLabel ?? other.TriggerLabel,
            TriggerType ?? other.TriggerType,
            CommandType ?? other.CommandType,
            HandlerType ?? other.HandlerType,
            unionTypes(EmittedEvents, other.EmittedEvents),
            unionTypes(ProjectionTypes, other.ProjectionTypes),
            unionTypes(ReadModelTypes, other.ReadModelTypes))
        {
            Pattern = Pattern ?? other.Pattern,
            TriggerKind = TriggerKind ?? other.TriggerKind,
            TriggerOrigin = TriggerOrigin ?? other.TriggerOrigin,
            AggregateTypes = unionTypes(AggregateTypes, other.AggregateTypes),
            PublishedMessages = unionTypes(PublishedMessages, other.PublishedMessages),
            ExternalSystems = union(ExternalSystems, other.ExternalSystems, x => $"{x.Direction}:{x.Name}"),
            Hotspots = union(Hotspots, other.Hotspots, x => $"{x.Origin}:{x.Text}"),
            Specifications = union(Specifications, other.Specifications, x => x.Identity),
            Domain = Domain ?? other.Domain,
        };
    }

    private static IReadOnlyList<TypeDescriptor> unionTypes(IReadOnlyList<TypeDescriptor> first, IReadOnlyList<TypeDescriptor> second)
        => union(first, second, x => x.FullName);

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

        EventModelElement add(EventModelElement element)
        {
            elements.Add(element);
            return element;
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
            trigger = add(EventModelElement.ForType(Name, EventModelElementKind.Trigger, TriggerType));
        }
        else if (TriggerLabel is not null)
        {
            trigger = add(EventModelElement.ForLabel(Name, EventModelElementKind.Trigger, TriggerLabel));
        }
        else if (TriggerOrigin?.Label is not null)
        {
            trigger = add(EventModelElement.ForLabel(Name, EventModelElementKind.Trigger, TriggerOrigin.Label));
        }

        var inboundSystems = ExternalSystems
            .Where(x => x.Direction == ExternalSystemDirection.Inbound)
            .Select(x => add(EventModelElement.ForLabel(Name, EventModelElementKind.ExternalSystem, x.Name)))
            .ToList();

        foreach (var hotspot in Hotspots)
        {
            add(EventModelElement.ForLabel(Name, EventModelElementKind.Hotspot, hotspot.Text));
        }

        // Command lane: command, handler, aggregates
        var command = CommandType is null ? null : add(EventModelElement.ForType(Name, EventModelElementKind.Command, CommandType));
        var handler = HandlerType is null ? null : add(EventModelElement.ForType(Name, EventModelElementKind.Handler, HandlerType));
        var aggregates = AggregateTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Aggregate, x))).ToList();

        // Event stream lane: emitted events, published messages
        var events = EmittedEvents.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Event, x))).ToList();
        var messages = PublishedMessages.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Message, x))).ToList();

        // Read model lane: projections, read models
        var projections = ProjectionTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Projection, x))).ToList();
        var readModels = ReadModelTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.ReadModel, x))).ToList();

        var outboundSystems = ExternalSystems
            .Where(x => x.Direction == ExternalSystemDirection.Outbound)
            .Select(x => add(EventModelElement.ForLabel(Name, EventModelElementKind.ExternalSystem, x.Name)))
            .ToList();

        // Edges. The "processor" is the handler when there is one, else the command.
        var entry = command ?? handler;
        var processor = handler ?? command;

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
            foreach (var evt in events)
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
            foreach (var evt in events)
            foreach (var readModel in readModels)
            {
                link(evt, readModel);
            }
        }

        // A view slice with no events of its own reads straight from its read models
        if (events.Count == 0 && processor is null && trigger is not null)
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
/// binary-compatible; <see cref="Aggregates"/> is an additive <c>init</c> property.
/// Use <see cref="Merge"/> to assemble the full picture from several sources.
/// </remarks>
/// <param name="Name">Display name of the model.</param>
/// <param name="Slices">Slices that make up the model, in declaration order.</param>
public sealed record EventModelDescriptor(
    string Name,
    IReadOnlyList<EventModelSliceDescriptor> Slices)
{
    /// <summary>
    /// The aggregate elements of the model — one per aggregate-shaped CLR type, with its kind and
    /// applied events. Slices point at these through
    /// <see cref="EventModelSliceDescriptor.AggregateTypes"/>.
    /// </summary>
    public IReadOnlyList<AggregateDescriptor> Aggregates { get; init; } = Array.Empty<AggregateDescriptor>();

    /// <summary>
    /// Assemble one model from several sources' descriptors. Slices with the same name are
    /// folded with <see cref="EventModelSliceDescriptor.Merge"/> in the order the descriptors
    /// are given (earlier wins on scalars — put derived sources before the overlay); aggregates
    /// are unioned by type. Slice order is first appearance.
    /// </summary>
    /// <param name="name">Name of the assembled model.</param>
    /// <param name="descriptors">Descriptors to fold, derived sources first.</param>
    public static EventModelDescriptor Merge(string name, IEnumerable<EventModelDescriptor> descriptors)
    {
        var slices = new List<EventModelSliceDescriptor>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var aggregates = new List<AggregateDescriptor>();
        var aggregateNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
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

            foreach (var aggregate in descriptor.Aggregates)
            {
                if (aggregateNames.Add(aggregate.Type.FullName)) aggregates.Add(aggregate);
            }
        }

        return new EventModelDescriptor(name, slices) { Aggregates = aggregates };
    }
}
