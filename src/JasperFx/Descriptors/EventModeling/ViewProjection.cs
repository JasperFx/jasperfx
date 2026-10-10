namespace JasperFx.Events.EventModeling;

/// <summary>
/// How a view slice's read model is projected (JasperFx/wolverine#4865) — not Event Modeling proper, but
/// what code generation needs to know about a view. Carried on <see cref="EventModelSliceDescriptor.ViewProjection"/>.
/// </summary>
/// <remarks>
/// Goes over the wire as an integer under the STJ defaults, so append new members; never renumber.
/// </remarks>
public enum ViewProjection
{
    /// <summary>One read model per stream, folded from that stream's events. The default.</summary>
    SingleStream = 0,

    /// <summary>One read model per identity that events from many streams are grouped into.</summary>
    MultiStream = 1,

    /// <summary>
    /// Folded, like a single-stream projection, over the events a Dynamic Consistency Boundary tag query
    /// selects rather than over one stream.
    /// </summary>
    DcbModel = 2,
}
