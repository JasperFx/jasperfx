namespace JasperFx.Events.EventModeling;

/// <summary>
/// The four canonical Event Modeling slice patterns, plus <see cref="Query"/>. Every slice in an
/// <see cref="EventModelDescriptor"/> is one of these once a source has
/// derived it (jasperfx#687 decision 1). The pattern drives lane rendering
/// and tells a viewer which roles to expect on the slice.
/// </summary>
/// <remarks>
/// The pattern is <em>derived</em> by the source that produced the slice —
/// Wolverine from its handler / HTTP / gRPC chains, the Bobcat generator from a
/// feature's tags — never hand-declared through the overlay. A slice whose
/// pattern has not been derived yet carries <see langword="null"/>.
/// <para>
/// Goes over the wire as an integer under the STJ defaults, so append new members; never renumber.
/// </para>
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

    /// <summary>
    /// A read with no state change: trigger → read of a read model, aggregate or document → response
    /// (jasperfx#1003). An HTTP <c>GET</c>, a gRPC read, a query message handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extends classic Event Modeling, which folds a read into the <see cref="View"/> slice. A View is
    /// <em>events → projection → read model</em>; a Query is the code that reads it, with its own trigger,
    /// specifications and drilldown. A Query slice names what it reads in
    /// <see cref="EventModelSliceDescriptor.ReadsFrom"/>, which links it to the View slice producing that
    /// read model (<see cref="EventModelLinkKind.ReadModelRead"/>).
    /// </para>
    /// <para>
    /// A Query emits no events and sends no command. One that does is a
    /// <see cref="HotspotOrigin.PatternViolation"/> hotspot
    /// (<see cref="EventModelSliceDescriptor.PatternViolations"/>).
    /// </para>
    /// <para>
    /// Appended last so the existing members keep their wire values.
    /// </para>
    /// </remarks>
    Query,
}
