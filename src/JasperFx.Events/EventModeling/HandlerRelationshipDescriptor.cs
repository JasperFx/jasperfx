using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Diagnostic descriptor for one handler's contribution to the
/// event-model graph. Captures the static command→events relationship the
/// CritterWatch swim-lane (CritterWatch#143 / #147) needs to render arrows
/// between commands, handlers, aggregates, and downstream events.
/// </summary>
/// <remarks>
/// One descriptor per handler chain the CritterWatch source generator
/// (CritterWatch#144) discovers via the standard Wolverine handler-discovery
/// conventions, plus the saga-subclass + <c>yield return new SomeEvent(...)</c>
/// patterns called out in the issue body. Emitted into the per-project
/// <c>CritterWatchAppManifest</c> as <c>static readonly</c> data; runtime
/// aggregation across all loaded <c>CritterWatchAppManifest</c> partials
/// produces the unified <see cref="EventModelDescriptor"/> the swim-lane
/// consumes.
/// </remarks>
/// <param name="HandlerType">CLR type of the handler class.</param>
/// <param name="MessageType">CLR type of the message the handler accepts.</param>
/// <param name="EmittedEvents">
///     Event types the handler's body emits (cascading message return,
///     <c>yield return</c>, <c>slice.PublishMessage</c>, etc.), in source order.
///     Entries with a null underlying type identity correspond to <c>yield return</c>
///     calls the generator couldn't statically resolve — rendered as a
///     <c>?</c> placeholder on the swim-lane rather than failing the build.
/// </param>
/// <param name="TargetAggregate">
///     When the handler is keyed to an aggregate type (<c>[WriteAggregate]</c>,
///     <c>[ReadAggregate]</c>, <c>[ConsistentAggregate]</c>, <c>[BoundaryModel]</c>),
///     the aggregate's CLR type. Null for stateless handlers that don't load
///     an aggregate snapshot.
/// </param>
public sealed record HandlerRelationshipDescriptor(
    TypeDescriptor HandlerType,
    TypeDescriptor MessageType,
    IReadOnlyList<TypeDescriptor> EmittedEvents,
    TypeDescriptor? TargetAggregate)
{
    /// <summary>
    /// How the emitting code is triggered. Defaults to <see cref="PublisherKind.Handler"/>
    /// — the historical behavior where the trigger is the incoming <see cref="MessageType"/>.
    /// Non-<c>Handler</c> kinds (HTTP/gRPC endpoints, projection side-effects, scheduled work)
    /// carry their trigger detail in <see cref="Origin"/>.
    /// </summary>
    /// <remarks>
    /// Added in JasperFx 2.11 as an additive <c>init</c> property so existing precompiled
    /// callers that use the positional constructor keep working unchanged
    /// (the descriptor remains binary-compatible). Lets the CritterWatch event-model
    /// graph distinguish a message-driven handler from a route-driven endpoint or a
    /// projection side-effect that publishes without an inbound message.
    /// </remarks>
    public PublisherKind Kind { get; init; } = PublisherKind.Handler;

    /// <summary>
    /// Polymorphic trigger origin for non-message publishers — the HTTP route + verb,
    /// gRPC service + method, or projection that initiates the emission. Null when
    /// <see cref="Kind"/> is <see cref="PublisherKind.Handler"/>, in which case the
    /// trigger is the <see cref="MessageType"/>.
    /// </summary>
    public PublisherOrigin? Origin { get; init; }
}
