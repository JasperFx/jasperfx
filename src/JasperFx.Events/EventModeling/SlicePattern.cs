namespace JasperFx.Events.EventModeling;

/// <summary>
/// The four canonical Event Modeling slice patterns. Every slice in an
/// <see cref="EventModelDescriptor"/> is one of these once a source has
/// derived it (jasperfx#687 decision 1). The pattern drives lane rendering
/// and tells a viewer which roles to expect on the slice.
/// </summary>
/// <remarks>
/// The pattern is <em>derived</em> by the source that produced the slice —
/// Wolverine from its handler / HTTP / gRPC chains, the Bobcat generator from a
/// feature's tags — never hand-declared through the overlay. A slice whose
/// pattern has not been derived yet carries <see langword="null"/>.
/// </remarks>
public enum SlicePattern
{
    /// <summary>
    /// A state change: trigger → command → handler (with its aggregate(s)) → emitted events.
    /// </summary>
    Command,

    /// <summary>
    /// A read: events → projection → read model, consumed by a UI or another slice.
    /// </summary>
    View,

    /// <summary>
    /// A reaction with no human in the loop: events or a schedule → processor → command / message.
    /// </summary>
    Automation,

    /// <summary>
    /// An integration edge: an external system's messages translated into the model's
    /// events, or the model's events translated out to an external system.
    /// </summary>
    Translation,
}
