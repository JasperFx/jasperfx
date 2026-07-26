namespace JasperFx.Events.Daemon;

/// <summary>
/// jasperfx#565: what KIND of failure stopped or paused a projection/subscription shard. A supervisor
/// outside the daemon (Wolverine's assignment plane, CritterWatch) can only see <see cref="AgentStatus"/>
/// today, which says a shard is paused but never why — and the operator response is completely different
/// per category: a poison event needs a code fix or a skip, a serialization failure needs a serializer or
/// data fix, an unknown event type is usually a deployment/registration gap, and an out-of-order
/// progression means two processes are racing the same shard.
/// </summary>
public enum ShardFailureCategory
{
    /// <summary>
    /// User projection/subscription code threw while applying an event — the classic "poison pill".
    /// Carries the failing event (see <see cref="ApplyEventException"/>). Turning on
    /// <see cref="ErrorHandlingOptions.SkipApplyErrors"/> converts this into a dead letter instead
    /// of a pause.
    /// </summary>
    ApplyEvent,

    /// <summary>
    /// The event store could not deserialize or upcast a persisted event body. The store's own
    /// exception (e.g. Marten's <c>EventDeserializationFailureException</c>) reports this category
    /// through <see cref="IEventFailureContext"/>. Governed by
    /// <see cref="ErrorHandlingOptions.SkipSerializationErrors"/>.
    /// </summary>
    EventSerialization,

    /// <summary>
    /// An event's stored type alias resolves to no known .NET type in THIS deployment — usually a
    /// missing registration or a rollback to a version that predates the event type, rather than
    /// bad data. Kept separate from <see cref="EventSerialization"/> because the operator response
    /// differs. Governed by <see cref="ErrorHandlingOptions.SkipUnknownEvents"/>.
    /// </summary>
    UnknownEventType,

    /// <summary>
    /// The shard's progression row moved underneath it
    /// (<see cref="ProgressionProgressOutOfOrderException"/>), which almost always means two
    /// processes are running the same shard. Note that the daemon <em>stops</em> rather than pauses
    /// on this one.
    /// </summary>
    ProgressionOutOfOrder,

    /// <summary>
    /// Anything else: a database outage, a timeout, a bug in the daemon itself. No single event can
    /// be blamed, so <see cref="ShardFailure.Event"/> is null.
    /// </summary>
    Other
}
