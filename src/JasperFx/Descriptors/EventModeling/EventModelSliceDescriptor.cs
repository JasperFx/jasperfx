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

        // jasperfx#824. The two new lists merge as every other type list does; Chapter merges as
        // Domain does -- first non-null wins on a tie, and a genuine disagreement becomes a
        // SourceDisagreement hotspot rather than a silent drop.
        var consumedEvents = mergeTypes(EventModelRole.ConsumedEvents, ConsumedEvents, other.ConsumedEvents);
        var readsFrom = mergeTypes(EventModelRole.ReadsFrom, ReadsFrom, other.ReadsFrom);
        var chapter = mergeScalar(EventModelRole.Chapter, Chapter, other.Chapter, x => x);

        // jasperfx#836. Origin merges as any other scalar does, which gives the store dimension the
        // one thing it was missing: two sources that contributed the SAME slice from DIFFERENT stores
        // now leave a SourceDisagreement naming both stores, instead of the survivor carrying nothing
        // to say where it came from. A declared slice claims no origin, so it never takes one away.
        var origin = mergeScalar(EventModelRole.Origin, Origin, other.Origin, x => x.OriginalString);

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
            ConsumedEvents = consumedEvents,
            ReadsFrom = readsFrom,
            Chapter = chapter,
            Origin = origin,
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

        // jasperfx#824. A consumed event is the same sticky repeated where it is read, so an event a
        // slice both emits and consumes is ONE element -- the ids collide by construction, and adding
        // it twice would put two overlapping stickies on the canvas and double every edge off it.
        // Consumed events feed the projection / read model edges below exactly as emitted ones do;
        // what they do NOT get is an edge from the processor, because the slice did not write them.
        var consumed = ConsumedEvents
            .Select(x => EventModelElement.ForType(Name, EventModelElementKind.Event, x))
            .Where(x => events.All(e => e.Id != x.Id))
            .Select(x => add(x, EventModelRole.ConsumedEvents))
            .ToList();

        var inboundEvents = events.Concat(consumed).ToList();

        // Read model lane: projections, read models
        var projections = ProjectionTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.Projection, x))).ToList();
        var readModels = ReadModelTypes.Select(x => add(EventModelElement.ForType(Name, EventModelElementKind.ReadModel, x))).ToList();

        // Same dedupe rule as consumed events, for the same reason: a slice that both reads and
        // produces one read model draws one sticky.
        var readsFrom = ReadsFrom
            .Select(x => EventModelElement.ForType(Name, EventModelElementKind.ReadModel, x))
            .Where(x => readModels.All(r => r.Id != x.Id))
            .Select(x => add(x, EventModelRole.ReadsFrom))
            .ToList();

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
    /// Hotspots that belong to the model rather than to any one slice — the open question that
    /// spans the whole flow, declared through the overlay with <c>Hotspot("…")</c> on the
    /// <c>EventModelBuilder</c> (jasperfx#690). Hotspots about a single slice live on
    /// <see cref="EventModelSliceDescriptor.Hotspots"/> instead, where they render in that slice's
    /// wireframe lane.
    /// </summary>
    public IReadOnlyList<HotspotDescriptor> Hotspots { get; init; } = Array.Empty<HotspotDescriptor>();

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
