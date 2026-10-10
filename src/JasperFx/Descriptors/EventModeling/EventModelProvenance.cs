namespace JasperFx.Events.EventModeling;

/// <summary>
/// How much authority a source's claim about an Event Model carries — the ladder that
/// decides precedence when several sources describe the same slice (jasperfx#703).
/// </summary>
/// <remarks>
/// <para>
/// <b>Higher rung wins.</b> Four producers now feed one <see cref="EventModelDescriptor"/>: Gherkin
/// specs, the C# overlay / code-first specs, Wolverine's chains, and runtime observation from
/// CritterWatch. Something has to arbitrate, and "whoever registered first" is neither readable nor
/// defensible. Production beats what the code implies, and the code beats what somebody wrote down.
/// </para>
/// <para>
/// ⚠️ <b>This inverts the ordering that shipped before jasperfx#703</b>, where
/// <c>WolverineEventModelSource</c> was registered at index 0 specifically so derived roles would
/// beat overlays. Registration order is no longer the mechanism; the ladder is. Order now only
/// breaks ties between sources on the same rung.
/// </para>
/// <para>
/// ⚠️ <b>Precedence is per claimed role, not wholesale</b> — see
/// <see cref="EventModelSliceDescriptor.ProvenanceFor"/>. A source that does not claim a role never
/// overrides one that does, so slice names, domains and specification links keep coming from
/// declarations and keep winning by default: nothing else claims them.
/// </para>
/// <para>
/// <b>Relationship to CritterWatch's <c>LifecycleProvenance</c>.</b> The two vocabularies overlap but
/// are not the same axis, and conflating them is a trap. CritterWatch's <c>Inferred | Observed |
/// Confirmed</c> is a <em>reconciliation</em> of one edge across static and runtime discovery:
/// <c>Confirmed</c> means "both sources agree", not "seen in production". This enum is a ladder of
/// authority instead. <see cref="Declared"/> and <see cref="Derived"/> both map onto CritterWatch's
/// <c>Inferred</c>, <see cref="Observed"/> onto its <c>Observed</c>, and its <c>Confirmed</c> has no
/// rung here — agreement between rungs is expressed by the <em>absence</em> of a disagreement
/// hotspot (jasperfx#704) rather than by a fourth value.
/// </para>
/// </remarks>
public enum EventModelProvenance
{
    /// <summary>
    /// Somebody wrote it down — a Gherkin spec, a code-first specification, or the C# overlay's
    /// <c>EventModelDefinition</c>. The lowest rung, and the only source of slice names and domains;
    /// specification links can also come from <see cref="Specified"/> (jasperfx#995).
    /// </summary>
    Declared = 0,

    /// <summary>
    /// Read out of the code — Wolverine's handler / HTTP / gRPC chains, the source generator's view
    /// of a projection. Beats a declaration, because the code is what actually ships.
    /// </summary>
    Derived = 1,

    /// <summary>
    /// Seen happening in a running system — CritterWatch's runtime observation of appends and
    /// causations. The top rung: production is the only source that can see a path static analysis
    /// missed, and it cannot be argued with.
    /// </summary>
    Observed = 2,

    /// <summary>
    /// Read off the specifications — a spec manifest saying which command each specification exercises,
    /// joined onto the model's slices by command type (jasperfx#995). Ranks <em>between</em>
    /// <see cref="Declared"/> and <see cref="Derived"/>: a specification is evidence that something was
    /// written against the slice, which beats a hand-typed link, while the code still beats both.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Numbered 3, ranked second.</b> This enum goes over the wire, so a new rung is appended rather
    /// than slotted in, and its integer no longer says where it sits. Compare rungs with
    /// <see cref="EventModelProvenanceRanking.Rank"/> / <see cref="EventModelProvenanceRanking.Outranks"/>,
    /// never with <c>&gt;</c> on the enum.
    /// </remarks>
    Specified = 3,
}

/// <summary>
/// The order of the <see cref="EventModelProvenance"/> ladder. The enum's integers are wire values, not
/// ranks, since <see cref="EventModelProvenance.Specified"/> was appended above the top rung's number
/// while ranking below it (jasperfx#995).
/// </summary>
public static class EventModelProvenanceRanking
{
    /// <summary>Position on the ladder, lowest first: Declared, Specified, Derived, Observed.</summary>
    public static int Rank(this EventModelProvenance provenance) => provenance switch
    {
        EventModelProvenance.Declared => 0,
        EventModelProvenance.Specified => 1,
        EventModelProvenance.Derived => 2,
        EventModelProvenance.Observed => 3,
        _ => -1,
    };

    /// <summary>Whether <paramref name="provenance"/> sits on a higher rung than <paramref name="other"/>.</summary>
    public static bool Outranks(this EventModelProvenance provenance, EventModelProvenance other)
        => provenance.Rank() > other.Rank();
}

/// <summary>
/// The roles of an <see cref="EventModelSliceDescriptor"/> that provenance is tracked against — one
/// entry per mergeable member, so precedence can be decided per claimed role rather than wholesale
/// (jasperfx#703).
/// </summary>
/// <remarks>
/// A role is <em>claimed</em> when the slice carries a value for it: a non-null scalar, or a non-empty
/// list. That definition is deliberately structural rather than something a source has to opt into,
/// so every existing source gets correct per-role attribution without changing a line.
/// </remarks>
public enum EventModelRole
{
    /// <summary><see cref="EventModelSliceDescriptor.TriggerLabel"/>.</summary>
    TriggerLabel,

    /// <summary><see cref="EventModelSliceDescriptor.TriggerType"/>.</summary>
    TriggerType,

    /// <summary><see cref="EventModelSliceDescriptor.TriggerKind"/>.</summary>
    TriggerKind,

    /// <summary><see cref="EventModelSliceDescriptor.TriggerOrigin"/>.</summary>
    TriggerOrigin,

    /// <summary><see cref="EventModelSliceDescriptor.Pattern"/>.</summary>
    Pattern,

    /// <summary><see cref="EventModelSliceDescriptor.CommandType"/>.</summary>
    CommandType,

    /// <summary><see cref="EventModelSliceDescriptor.HandlerType"/>.</summary>
    HandlerType,

    /// <summary><see cref="EventModelSliceDescriptor.AggregateTypes"/>.</summary>
    AggregateTypes,

    /// <summary><see cref="EventModelSliceDescriptor.EmittedEvents"/>.</summary>
    EmittedEvents,

    /// <summary><see cref="EventModelSliceDescriptor.PublishedMessages"/>.</summary>
    PublishedMessages,

    /// <summary><see cref="EventModelSliceDescriptor.ProjectionTypes"/>.</summary>
    ProjectionTypes,

    /// <summary><see cref="EventModelSliceDescriptor.ReadModelTypes"/>.</summary>
    ReadModelTypes,

    /// <summary><see cref="EventModelSliceDescriptor.ExternalSystems"/>.</summary>
    ExternalSystems,

    /// <summary><see cref="EventModelSliceDescriptor.Hotspots"/>.</summary>
    Hotspots,

    /// <summary><see cref="EventModelSliceDescriptor.Specifications"/>.</summary>
    Specifications,

    /// <summary><see cref="EventModelSliceDescriptor.Domain"/>.</summary>
    Domain,

    /// <summary><see cref="EventModelSliceDescriptor.ConsumedEvents"/> (jasperfx#824).</summary>
    ConsumedEvents,

    /// <summary><see cref="EventModelSliceDescriptor.ReadsFrom"/> (jasperfx#824).</summary>
    ReadsFrom,

    /// <summary><see cref="EventModelSliceDescriptor.Chapter"/> (jasperfx#824).</summary>
    Chapter,

    /// <summary>
    /// <see cref="EventModelSliceDescriptor.Origin"/> — which source, as against which rung
    /// (jasperfx#836).
    /// </summary>
    /// <remarks>
    /// Appended rather than slotted in beside the rest: this enum goes over the wire as an integer
    /// under the STJ defaults, so renumbering an existing member would silently re-label every
    /// disagreement hotspot a running console already holds.
    /// </remarks>
    Origin,

    /// <summary>
    /// <see cref="EventModelSliceDescriptor.StartsStream"/> — the aggregate whose stream the slice
    /// starts (jasperfx#957). Appended for the same wire reason as <see cref="Origin"/>.
    /// </summary>
    StartsStream,

    /// <summary>
    /// <see cref="EventModelSliceDescriptor.AggregateDeclaration"/> — why the slice has the aggregate it
    /// has (jasperfx#994). Appended for the same wire reason as <see cref="Origin"/>.
    /// </summary>
    AggregateDeclaration,

    /// <summary>
    /// <see cref="EventModelSliceDescriptor.DeciderModel"/> — the DCB decider model the slice decides
    /// through (jasperfx#994). Appended for the same wire reason as <see cref="Origin"/>.
    /// </summary>
    DeciderModel,

    /// <summary>
    /// <see cref="EventModelSliceDescriptor.ViewProjection"/> — how a view's read model is projected
    /// (JasperFx/wolverine#4865). Appended for the same wire reason as <see cref="Origin"/>.
    /// </summary>
    ViewProjection,
}
