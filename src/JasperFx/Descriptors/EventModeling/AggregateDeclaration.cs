namespace JasperFx.Events.EventModeling;

/// <summary>
/// Why an Event Model slice has the aggregate it has, as a declaration said it (jasperfx#994). Carried on
/// <see cref="EventModelSliceDescriptor.AggregateDeclaration"/> so tooling can explain a slice's aggregate
/// rather than only show it.
/// </summary>
/// <remarks>
/// Goes over the wire as an integer under the STJ defaults, so append new members; never renumber.
/// </remarks>
public enum AggregateDeclaration
{
    /// <summary>
    /// Applied from the definition's <c>ForAggregate</c> default to a command slice that declared nothing
    /// of its own.
    /// </summary>
    Default = 0,

    /// <summary>Declared on the slice itself, with <c>Against</c> or <c>StartsStream</c>.</summary>
    Explicit = 1,

    /// <summary>
    /// Deliberately none (<c>NoAggregate()</c>) — legitimate only for a slice that purely starts a stream
    /// or decides against nothing. Tooling stops warning about a missing aggregate.
    /// </summary>
    None = 2,

    /// <summary>
    /// Decides through a Dynamic Consistency Boundary decider model rather than single-stream aggregates;
    /// see <see cref="EventModelSliceDescriptor.DeciderModel"/>.
    /// </summary>
    DeciderModel = 3,
}
