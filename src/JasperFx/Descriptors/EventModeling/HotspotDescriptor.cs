namespace JasperFx.Events.EventModeling;

/// <summary>
/// Where a hotspot came from.
/// </summary>
public enum HotspotOrigin
{
    /// <summary>
    /// A specification bound to the slice is pending — it cannot pass yet, or has no bound
    /// steps. The primary hotspot mechanism (jasperfx#689): an open question in a spec-driven
    /// flow <em>is</em> a pending spec, so the hotspot comes from the spec, not from a builder call.
    /// </summary>
    PendingSpecification,

    /// <summary>
    /// Free-text prose declared through the overlay with <c>Hotspot("…")</c> on a slice or on the
    /// model (jasperfx#690) — the open question that has no specification behind it yet. Sources
    /// may emit it too.
    /// </summary>
    Prose,

    /// <summary>
    /// Two sources describing the same slice made different claims about the same role, and
    /// <see cref="EventModelSliceDescriptor.Merge"/> had to drop one of them (jasperfx#704).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <em>The code says this slice emits <c>OrderPlaced</c>; production says it appends
    /// <c>OrderPlaced</c> <b>and</b> <c>AuditRecorded</c></em> is arguably the most valuable thing a
    /// four-source model can tell you, and a first-wins merge destroyed exactly that signal — the
    /// losing claim vanished with no trace. This origin records it instead.
    /// </para>
    /// <para>
    /// Emitted by <see cref="EventModelSliceDescriptor.Merge"/> rather than by any source, and only
    /// when a claim is actually lost. Two sources on the same rung whose lists simply union have not
    /// disagreed about anything, so nothing is recorded — a model with no disagreements is identical
    /// to one produced before jasperfx#704.
    /// </para>
    /// </remarks>
    SourceDisagreement,
}

/// <summary>
/// One source's claim about one role, as it stood before a merge resolved the disagreement
/// (jasperfx#704).
/// </summary>
/// <param name="Provenance">The rung the claim came from — enough to see which source to trust.</param>
/// <param name="Value">
///     Display rendering of what was claimed: short type names for the typed roles, the value itself
///     for the scalar ones.
/// </param>
public sealed record EventModelClaim(EventModelProvenance Provenance, string Value);

/// <summary>
/// A hotspot — an open question, a conflict, or (primarily) a specification that is still
/// pending. Attached to a slice, where it renders in the canonical hotspot colour in the
/// wireframe lane, or to the whole model when the question is not about one slice
/// (jasperfx#687 decision 6, jasperfx#689, jasperfx#690).
/// </summary>
/// <param name="Origin">Whether this hotspot is a pending specification or prose.</param>
/// <param name="Text">
///     Display text. For a pending specification this is the spec identity
///     (<c>{Feature}/{Scenario}</c>); for prose it is the note itself.
/// </param>
/// <param name="SpecificationIdentity">
///     The <c>{Feature}/{Scenario}</c> identity of the pending specification, when
///     <see cref="Origin"/> is <see cref="HotspotOrigin.PendingSpecification"/>; otherwise null.
/// </param>
public sealed record HotspotDescriptor(
    HotspotOrigin Origin,
    string Text,
    string? SpecificationIdentity = null)
{
    /// <summary>
    /// The role two sources disagreed about, when <see cref="Origin"/> is
    /// <see cref="HotspotOrigin.SourceDisagreement"/>; otherwise null (jasperfx#704).
    /// </summary>
    public EventModelRole? Role { get; init; }

    /// <summary>
    /// The claim the merge kept, when <see cref="Origin"/> is
    /// <see cref="HotspotOrigin.SourceDisagreement"/>; otherwise null (jasperfx#704).
    /// </summary>
    /// <remarks>
    /// <see cref="Text"/> already renders the same information for a viewer that only knows how to
    /// draw a hotspot. This and <see cref="LosingClaim"/> are the structured form, for a reader that
    /// wants to act on it — decide which source to trust and which to fix.
    /// </remarks>
    public EventModelClaim? WinningClaim { get; init; }

    /// <summary>
    /// The claim the merge dropped, when <see cref="Origin"/> is
    /// <see cref="HotspotOrigin.SourceDisagreement"/>; otherwise null (jasperfx#704).
    /// </summary>
    /// <remarks>
    /// A pair rather than a list because merges are pairwise: three sources disagreeing about one
    /// role produce two of these, each naming the two claims that actually met. Keeping it to two
    /// scalars also keeps <see cref="HotspotDescriptor"/>'s record equality value-based, which the
    /// merge's own de-duplication relies on.
    /// </remarks>
    public EventModelClaim? LosingClaim { get; init; }

    /// <summary>A hotspot for a specification that is pending (jasperfx#689).</summary>
    public static HotspotDescriptor PendingSpecification(string specificationIdentity)
        => new(HotspotOrigin.PendingSpecification, specificationIdentity, specificationIdentity);

    /// <summary>A free-text prose hotspot — see <see cref="HotspotOrigin.Prose"/> (jasperfx#690).</summary>
    public static HotspotDescriptor Prose(string text)
        => new(HotspotOrigin.Prose, text);

    /// <summary>
    /// A hotspot for two sources making different claims about <paramref name="role"/>
    /// (jasperfx#704). <paramref name="winner"/> is the claim the merge kept.
    /// </summary>
    public static HotspotDescriptor SourceDisagreement(EventModelRole role, EventModelClaim winner,
        EventModelClaim loser)
        => new(HotspotOrigin.SourceDisagreement,
            $"{role}: {winner.Provenance} claims {winner.Value}; {loser.Provenance} claims {loser.Value}")
        {
            Role = role,
            WinningClaim = winner,
            LosingClaim = loser,
        };
}
