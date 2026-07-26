namespace JasperFx.Events.Daemon;

/// <summary>
/// jasperfx#565: implemented by any exception that can name the single event it failed on, so the daemon
/// can classify a shard failure and report the offending event WITHOUT knowing the concrete exception
/// types of the store underneath it.
///
/// <para>
/// JasperFx.Events owns only one of these — <see cref="ApplyEventException"/>. The others live in the
/// stores, because that is where events are read and deserialized: Marten's
/// <c>EventDeserializationFailureException</c> / <c>UnknownEventTypeException</c> and Polecat's
/// equivalents implement this interface and declare their own <see cref="Category"/>. That is
/// deliberately the store's call rather than type-name sniffing in the daemon.
/// </para>
///
/// <para>
/// Only <see cref="Sequence"/> is guaranteed. A serialization failure is detected while reading a row,
/// before there is an <see cref="IEvent"/> to inspect, so it may know nothing but the sequence and the
/// stored type alias — every other member is nullable for exactly that reason. Whatever IS known lets a
/// consumer correlate the failure with a <see cref="DeadLetterEvent"/> row for the same
/// (projection, shard, sequence) if the event is later skipped.
/// </para>
/// </summary>
public interface IEventFailureContext
{
    /// <summary>
    /// How the daemon should classify a shard failure caused by this exception.
    /// </summary>
    ShardFailureCategory Category { get; }

    /// <summary>
    /// Store-wide sequence number of the failing event. The one member every implementation can supply,
    /// and the key a consumer joins on to find a matching <see cref="DeadLetterEvent"/>.
    /// </summary>
    long Sequence { get; }

    /// <summary>
    /// The event store's type alias for the failing event (e.g. <c>trip_started</c>), when known.
    /// </summary>
    string? EventTypeName { get; }

    /// <summary>
    /// Unique id of the failing event, when the exception was raised late enough to have one.
    /// </summary>
    Guid? EventId { get; }

    /// <summary>
    /// Stream id of the failing event for Guid-identified streams, when known.
    /// </summary>
    Guid? StreamId { get; }

    /// <summary>
    /// Stream key of the failing event for string-identified streams, when known.
    /// </summary>
    string? StreamKey { get; }

    /// <summary>
    /// Tenant the failing event belongs to, when known. Part of the dead-letter correlation key on
    /// tenant-partitioned stores, where one shard accumulates failures per tenant.
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// Version of the failing event within its stream, when known.
    /// </summary>
    long? Version { get; }
}
