using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Structured trigger detail shared by <see cref="EventModelSliceDescriptor.TriggerOrigin"/>
/// and <see cref="HandlerRelationshipDescriptor.Origin"/> — one shape for both so the two
/// descriptors fold into one vocabulary (jasperfx#687). Historically: the polymorphic trigger
/// origin for a <see cref="HandlerRelationshipDescriptor"/> whose
/// <see cref="HandlerRelationshipDescriptor.Kind"/> is not
/// <see cref="PublisherKind.Handler"/> — i.e. publishers initiated by something other
/// than an inbound message (an HTTP route, a gRPC method, a projection side-effect,
/// or scheduled work).
/// </summary>
/// <remarks>
/// Modeled as a single flat record with optional, kind-specific fields rather than a
/// type hierarchy so it round-trips cleanly through both the source generator's emitted
/// C# literals and the JSON wire to the CritterWatch SPA without polymorphic-serialization
/// ceremony. Only the fields relevant to the owning descriptor's
/// <see cref="PublisherKind"/> are populated; the rest stay null. Added in JasperFx 2.11.
/// </remarks>
public sealed record PublisherOrigin
{
    /// <summary>The HTTP route template (e.g. <c>/orders/{id}</c>) for <see cref="PublisherKind.HttpEndpoint"/>.</summary>
    public string? HttpRoute { get; init; }

    /// <summary>The HTTP verb (e.g. <c>POST</c>) for <see cref="PublisherKind.HttpEndpoint"/>.</summary>
    public string? HttpMethod { get; init; }

    /// <summary>
    /// The HTTP endpoint's route name, when it has one (e.g. <c>GetOrder</c>) — what ASP.NET Core link
    /// generation and Wolverine's <c>HttpChain.RouteName</c> call it. Optional; null when the endpoint is
    /// unnamed or the source cannot see the name.
    /// </summary>
    /// <remarks>
    /// Merges like the route: a known name beats an unknown one, so a source that knows the name fills it in
    /// even when another source's origin wins, and two different known names leave a
    /// <see cref="HotspotOrigin.SourceDisagreement"/> on <see cref="EventModelRole.TriggerOrigin"/>.
    /// </remarks>
    public string? HttpRouteName { get; init; }

    /// <summary>The gRPC service name for <see cref="PublisherKind.GrpcEndpoint"/>.</summary>
    public string? GrpcService { get; init; }

    /// <summary>The gRPC method name for <see cref="PublisherKind.GrpcEndpoint"/>.</summary>
    public string? GrpcMethod { get; init; }

    /// <summary>
    /// The projection CLR type that raises the side-effect for
    /// <see cref="PublisherKind.ProjectionSideEffect"/>.
    /// </summary>
    public TypeDescriptor? ProjectionType { get; init; }

    /// <summary>
    /// A human-friendly label for the trigger when no structured field fits
    /// (e.g. a cron expression for <see cref="PublisherKind.Scheduled"/> or a free-form
    /// description for <see cref="PublisherKind.External"/>). Optional even for the kinds
    /// above, where it can carry a pre-rendered display string like <c>"POST /orders"</c>.
    /// </summary>
    public string? Label { get; init; }
}
