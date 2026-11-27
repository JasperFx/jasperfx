using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// What kind of thing a command slice reads before it decides (jasperfx#1297).
/// </summary>
/// <remarks>
/// Goes over the wire as an integer under the STJ defaults, so append new members; never renumber.
/// </remarks>
public enum BoundaryReadKind
{
    /// <summary>A decider model or aggregate, folded over the events the tag selects.</summary>
    Model = 0,

    /// <summary>A projection's read model, consulted by tag.</summary>
    Projection = 1,
}

/// <summary>
/// One declared read of a command slice: a model or a projection, selected by one tag whose value comes
/// from a member of the command (jasperfx#1297). See <see cref="EventModelSliceDescriptor.Reads"/>.
/// </summary>
/// <remarks>
/// <b>Reads are declared, the boundary is derived.</b> A command never declares its own consistency
/// boundary; the query and the append condition are computed from what it reads
/// (<see cref="EventModelSliceDescriptor.BoundaryQuery"/>). This is the dcb.events rule, and how Axon's
/// <c>@EventCriteriaBuilder</c> and ESDM's <c>consults</c> work.
/// </remarks>
/// <param name="Kind">Whether <paramref name="Type"/> is a model or a projection.</param>
/// <param name="Type">What is read.</param>
/// <param name="Tag">The tag the read selects by, as the model's <see cref="EventModelDescriptor.Tags"/> name it.</param>
/// <param name="Member">The command member whose value is the tag's value, or null when bound by convention.</param>
public sealed record BoundaryReadDescriptor(BoundaryReadKind Kind, TypeDescriptor Type, string Tag, string? Member = null);
