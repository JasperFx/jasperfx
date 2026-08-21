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
    /// Free-text prose declared through the overlay. Reserved form — the overlay surface for it
    /// is a later stage (jasperfx#690); sources may still emit it.
    /// </summary>
    Prose,
}

/// <summary>
/// A hotspot attached to a slice — an open question, a conflict, or (primarily) a
/// specification that is still pending. Renders in the canonical hotspot colour
/// on the slice it is attached to (jasperfx#687 decision 6, jasperfx#689).
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

    /// <summary>A prose hotspot. Reserved form — see <see cref="HotspotOrigin.Prose"/>.</summary>
    public static HotspotDescriptor Prose(string text)
        => new(HotspotOrigin.Prose, text);
}
