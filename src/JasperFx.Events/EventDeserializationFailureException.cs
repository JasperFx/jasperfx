using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events;

/// <summary>
///     Thrown when a store cannot deserialize (or upcast) a persisted event body out of its events
///     table.
/// </summary>
/// <remarks>
///     <para>
///         Lifted from the identically-shaped exceptions in Marten and Polecat. Stores subclass or
///         type-forward to this.
///     </para>
///     <para>
///         jasperfx#565: this exception declares its own <see cref="ShardFailureCategory" /> through
///         <see cref="IEventFailureContext" />, which is how a paused shard reports <em>why</em> it is
///         down. The daemon deliberately has no fallback — it never sniffs a store's exception type
///         names — so without this a corrupted event body classified as
///         <see cref="ShardFailureCategory.Other" /> with no event details at all. A body the store
///         could not deserialize is a serializer or data problem — governed by
///         <c>SkipSerializationErrors</c> — which is a different operator action from an unregistered
///         event type; see <see cref="UnknownEventTypeException" />.
///     </para>
/// </remarks>
public class EventDeserializationFailureException : Exception, IEventFailureContext
{
    public EventDeserializationFailureException(long sequence, string? eventTypeName, Exception innerException)
        : this($"Event deserialization error on sequence = {sequence} for event type {eventTypeName}",
            sequence, eventTypeName, innerException)
    {
    }

    public EventDeserializationFailureException(long sequence, IEventType eventType, Exception innerException)
        : this(sequence, eventType.EventTypeName, innerException)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected EventDeserializationFailureException(string message, long sequence, string? eventTypeName,
        Exception innerException) : base(message, innerException)
    {
        Sequence = sequence;
        EventTypeName = eventTypeName;
    }

    /// <summary>
    ///     Store-wide sequence number of the event whose body could not be read.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    ///     The event store's type alias for the failing event (e.g. <c>trip_started</c>), when the row
    ///     supplied one. Retained as data rather than being buried in the message string so the daemon
    ///     can report it on <see cref="ShardFailure" />.
    /// </summary>
    public string? EventTypeName { get; }

    public ShardFailureCategory Category => ShardFailureCategory.EventSerialization;

    // Everything below is raised while reading an events-table row, BEFORE there is an IEvent to
    // inspect, so nothing but the sequence and the stored type alias is knowable here.
    // IEventFailureContext makes every one of these nullable for exactly this case.
    Guid? IEventFailureContext.EventId => null;
    Guid? IEventFailureContext.StreamId => null;
    string? IEventFailureContext.StreamKey => null;
    string? IEventFailureContext.TenantId => null;
    long? IEventFailureContext.Version => null;

    /// <summary>
    ///     Build the <see cref="DeadLetterEvent" /> row recording this failure for the named shard.
    ///     Lifted from Marten's internal helper — it was built entirely from shared types.
    /// </summary>
    public DeadLetterEvent ToDeadLetterEvent(ShardName name)
    {
        return new DeadLetterEvent
        {
            // marten#5048 / jasperfx#565: assign the id here rather than leaving it to document identity
            // generation at write time, so the creating process knows the dead letter's id BEFORE the
            // (background, retried) write lands and can correlate it with the ShardFailure it reported.
            // Stores only generate an id when the value is empty, so pre-assigning changes nothing about
            // how the row persists. Version 7 keeps ids time-ordered, matching what jasperfx's
            // DeadLetterEvent constructor does on the ApplyEventException path.
            Id = Guid.CreateVersion7(),
            EventSequence = Sequence,
            ExceptionMessage = Message,
            ExceptionType = GetType().FullNameInCode(),
            ProjectionName = name.Name,
            ShardName = name.ShardKey
        };
    }
}
