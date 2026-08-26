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

        var claimedBy = new Dictionary<EventModelRole, EventModelProvenance>();
        var disagreements = new List<HotspotDescriptor>();

        // True when other's claim on this role outranks ours -- or when we make no claim at all.
        // False on a tie, which is what preserves the pre-jasperfx#703 first-wins behaviour.
        bool takeOther(EventModelRole role)
        {
            var mine = ProvenanceFor(role);
            var theirs = other.ProvenanceFor(role);

            var takeTheirs = theirs is not null && (mine is null || theirs > mine);

            if ((takeTheirs ? theirs : mine) is { } rung) claimedBy[role] = rung;

            return takeTheirs;
        }

        // Both sides claimed the role and the merge kept only one of them. Record what was lost.
        void disagree(EventModelRole role, bool tookTheirs, string mineValue, string theirsValue)
        {
            var mineClaim = new EventModelClaim(ProvenanceFor(role)!.Value, mineValue);
            var theirsClaim = new EventModelClaim(other.ProvenanceFor(role)!.Value, theirsValue);

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
            Func<T, string> key, Func<T, string> display)
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
                disagree(role, tookTheirs, render(mine, display), render(theirs, display));
            }

            return tookTheirs ? theirs : mine;
        }

        IReadOnlyList<TypeDescriptor> mergeTypes(EventModelRole role, IReadOnlyList<TypeDescriptor> mine,
            IReadOnlyList<TypeDescriptor> theirs)
            => mergeList(role, mine, theirs, x => x.FullName, x => x.Name);

        var triggerLabel = mergeScalar(EventModelRole.TriggerLabel, TriggerLabel, other.TriggerLabel, x => x);
        var triggerType = mergeScalar(EventModelRole.TriggerType, TriggerType, other.TriggerType, x => x.Name);
        var commandType = mergeScalar(EventModelRole.CommandType, CommandType, other.CommandType, x => x.Name);
        var handlerType = mergeScalar(EventModelRole.HandlerType, HandlerType, other.HandlerType, x => x.Name);
        var emittedEvents = mergeTypes(EventModelRole.EmittedEvents, EmittedEvents, other.EmittedEvents);
        var projectionTypes = mergeTypes(EventModelRole.ProjectionTypes, ProjectionTypes, other.ProjectionTypes);
        var readModelTypes = mergeTypes(EventModelRole.ReadModelTypes, ReadModelTypes, other.ReadModelTypes);
        var pattern = mergeValue(EventModelRole.Pattern, Pattern, other.Pattern);
        var triggerKind = mergeValue(EventModelRole.TriggerKind, TriggerKind, other.TriggerKind);
        var triggerOrigin = mergeScalar(EventModelRole.TriggerOrigin, TriggerOrigin, other.TriggerOrigin,
            x => x.Label ?? x.ToString());
        var aggregateTypes = mergeTypes(EventModelRole.AggregateTypes, AggregateTypes, other.AggregateTypes);
        var publishedMessages = mergeTypes(EventModelRole.PublishedMessages, PublishedMessages, other.PublishedMessages);
        var externalSystems = mergeList(EventModelRole.ExternalSystems, ExternalSystems, other.ExternalSystems,
            x => $"{x.Direction}:{x.Name}", x => x.Name);
        var specifications = mergeList(EventModelRole.Specifications, Specifications, other.Specifications,
            x => x.Identity, x => x.Identity);
        var domain = mergeScalar(EventModelRole.Domain, Domain, other.Domain, x => x);

        // Hotspots are annotations rather than claims about the system, so they always union: a
        // higher rung replacing the list would throw away the findings recorded here.
        takeOther(EventModelRole.Hotspots);
        var hotspots = union(union(Hotspots, other.Hotspots, hotspotKey), disagreements, hotspotKey);

        return new EventModelSliceDescriptor(Name, triggerLabel, triggerType, commandType, handlerType,
            emittedEvents, projectionTypes, readModelTypes)
        {
            Pattern = pattern,
            TriggerKind = triggerKind,
            TriggerOrigin = triggerOrigin,
            AggregateTypes = aggregateTypes,
            PublishedMessages = publishedMessages,
            ExternalSystems = externalSystems,
            Hotspots = hotspots,
            Specifications = specifications,
            Domain = domain,
            Provenance = higher(Provenance, other.Provenance),
            ClaimedBy = claimedBy,
        };
    }

    private static string hotspotKey(HotspotDescriptor hotspot) => $"{hotspot.Origin}:{hotspot.Text}";

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
            _ => second > first ? second : first,
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
    /// <summary>
    /// The aggregate elements of the model — one per aggregate-shaped CLR type, with its kind and
    /// applied events. Slices point at these through
    /// <see cref="EventModelSliceDescriptor.AggregateTypes"/>.
    /// </summary>
    public IReadOnlyList<AggregateDescriptor> Aggregates { get; init; } = Array.Empty<AggregateDescriptor>();

    /// <summary>
    /// Hotspots that belong to the model rather than to any one slice — the open question that
    /// spans the whole flow, declared through the overlay with <c>Hotspot("…")</c> on the
    /// <c>EventModelBuilder</c> (jasperfx#690). Hotspots about a single slice live on
    /// <see cref="EventModelSliceDescriptor.Hotspots"/> instead, where they render in that slice's
    /// wireframe lane.
    /// </summary>
    public IReadOnlyList<HotspotDescriptor> Hotspots { get; init; } = Array.Empty<HotspotDescriptor>();

    /// <summary>
    /// Stamp every unattributed slice with <paramref name="provenance"/> — the rung of the source
    /// that produced this descriptor (jasperfx#703). Slices a source attributed itself are left alone.
    /// </summary>
    public EventModelDescriptor WithProvenance(EventModelProvenance provenance)
        => this with { Slices = Slices.Select(x => x.WithProvenance(provenance)).ToList() };

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
        var aggregateNames = new HashSet<string>(StringComparer.Ordinal);
        var hotspots = new List<HotspotDescriptor>();
        var hotspotKeys = new HashSet<string>(StringComparer.Ordinal);

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

            foreach (var hotspot in descriptor.Hotspots)
            {
                if (hotspotKeys.Add($"{hotspot.Origin}:{hotspot.Text}")) hotspots.Add(hotspot);
            }
        }

        return new EventModelDescriptor(name, slices) { Aggregates = aggregates, Hotspots = hotspots };
    }
}
