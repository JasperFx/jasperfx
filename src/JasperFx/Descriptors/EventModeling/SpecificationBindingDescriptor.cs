using System.Text.Json.Serialization;
using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// One specification and what it exercises — the entry of a spec manifest, which lets the specifications
/// be a model source of their own: <see cref="EventModelSpecifications.Link"/> joins each entry onto the
/// slice that handles its command, so an <c>EventModelDefinition</c> needs no <c>LinksToSpecification</c>
/// at all (jasperfx#995).
/// </summary>
/// <remarks>
/// <para>
/// <b>In JasperFx so neither side depends on the other.</b> Bobcat emits these (bobcat#449) from what a
/// specification already says — <c>WhenReceived(Specify&lt;ApplyToVolunteer&gt;())</c> names the command —
/// and whatever runs the join reads them. Wolverine never references Bobcat, and Bobcat never references
/// Wolverine.
/// </para>
/// <para>
/// <b>The join key is the command type</b>, narrowed by <see cref="Domain"/> or <see cref="Namespace"/>
/// when several slices handle the same command — one per module in a modular monolith. An explicit
/// <see cref="SliceName"/> (Bobcat's slice marker) replaces the inference entirely.
/// </para>
/// </remarks>
/// <param name="Identity">The <c>{Feature}/{Scenario}</c> identity — the same key Bobcat publishes run evidence under.</param>
[method: JsonConstructor]
public sealed record SpecificationBindingDescriptor(string Identity)
{
    /// <summary>The command the specification sends. The join key; null when the spec names its slice explicitly.</summary>
    public TypeDescriptor? CommandType { get; init; }

    /// <summary>
    /// The domain (module) the specification exercises, for a command several domains handle. Matched
    /// against <see cref="EventModelSliceDescriptor.Domain"/>.
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// The namespace the specification exercises, for a command several modules handle and no domain
    /// tells apart. Matched as a prefix of the candidate slice's handler namespace, then of its command's.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// The slice the specification explicitly says it specifies — Bobcat's slice marker. Replaces the
    /// inference by command type; when the model has no such slice the binding is reported, not dropped.
    /// </summary>
    public string? SliceName { get; init; }

    /// <summary>
    /// CLR types the specification's steps resolved, in step order, carried onto the
    /// <see cref="SpecificationDescriptor"/> the join stamps. Never null.
    /// </summary>
    public IReadOnlyList<TypeDescriptor> ResolvedTypes { get; init; } = Array.Empty<TypeDescriptor>();
}
