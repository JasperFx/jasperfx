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
}

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
    /// <summary>A hotspot for a specification that is pending (jasperfx#689).</summary>
    public static HotspotDescriptor PendingSpecification(string specificationIdentity)
        => new(HotspotOrigin.PendingSpecification, specificationIdentity, specificationIdentity);

    /// <summary>A free-text prose hotspot — see <see cref="HotspotOrigin.Prose"/> (jasperfx#690).</summary>
    public static HotspotDescriptor Prose(string text)
        => new(HotspotOrigin.Prose, text);
}
