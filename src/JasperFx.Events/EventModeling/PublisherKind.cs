namespace JasperFx.Events.EventModeling;

/// <summary>
/// Classifies how a <see cref="HandlerRelationshipDescriptor"/>'s emitting code is
/// triggered, so the CritterWatch event-model graph can lane and label publishers
/// that don't fit the classic "handler reacts to an inbound message" shape.
/// </summary>
/// <remarks>
/// Added in JasperFx 2.11. Default-valued (<see cref="Handler"/> is <c>0</c>) so the
/// historical positional-constructor descriptors deserialize/round-trip unchanged.
/// </remarks>
public enum PublisherKind
{
    /// <summary>
    /// Reacts to an incoming message — the trigger is the descriptor's
    /// <c>MessageType</c>. The historical, default behavior.
    /// </summary>
    Handler,

    /// <summary>An HTTP endpoint (e.g. Wolverine.Http route). Trigger detail lives in the origin's route + verb.</summary>
    HttpEndpoint,

    /// <summary>A gRPC service method. Trigger detail lives in the origin's service + method.</summary>
    GrpcEndpoint,

    /// <summary>
    /// An explicit, non-cascading bus call (<c>IMessageBus</c> / <c>IMessageContext</c>
    /// publish or send) that originates a message outside a handler-return cascade.
    /// </summary>
    DirectBusCall,

    /// <summary>A projection side-effect that publishes messages while applying events (e.g. <c>RaiseSideEffects</c>).</summary>
    ProjectionSideEffect,

    /// <summary>A scheduled or recurring trigger with no inbound message.</summary>
    Scheduled,

    /// <summary>An external or otherwise unclassified trigger.</summary>
    External
}
