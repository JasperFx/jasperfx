using JasperFx.Events.Daemon;

namespace JasperFx.Events;

/// <summary>
///     Thrown when an event's persisted .NET type name or alias resolves to no known event type in
///     this deployment.
/// </summary>
/// <remarks>
///     <para>
///         Lifted from the per-store exceptions of the same name in Marten, Polecat and Fisher, which
///         had converged on the same shape (jasperfx#565 gave all three the
///         <see cref="IEventFailureContext" /> contract). Stores subclass or type-forward to this; a
///         store whose tests pin a diverged message keeps it through the protected message-overriding
///         constructor.
///     </para>
///     <para>
///         Kept deliberately distinct from <see cref="ShardFailureCategory.EventSerialization" />. An
///         alias that resolves to no known .NET type is normally a missing registration or a
///         deployment rolled back past the event type's introduction — a deployment fix, not a data
///         fix — so an operator responds to it differently.
///     </para>
/// </remarks>
public class UnknownEventTypeException : Exception, IEventFailureContext
{
    /// <summary>
    ///     The sequence reported when the throw site had no event row in hand — e.g. resolving a .NET
    ///     type name outside the event read path. <see cref="IEventFailureContext.Sequence" /> is
    ///     non-nullable by contract, so a sentinel is unavoidable, and -1 is already how the stores'
    ///     event read paths spell "the sequence could not be determined".
    /// </summary>
    public const long UnknownSequence = -1;

    public UnknownEventTypeException(string? eventTypeName) : this(eventTypeName, UnknownSequence)
    {
    }

    /// <summary>
    ///     marten#5048 / jasperfx#565: carry the store-wide sequence of the offending event row when the
    ///     throw site knows it, so a shard paused by an unregistered event type can name the event that
    ///     stopped it instead of only its alias.
    /// </summary>
    public UnknownEventTypeException(string? eventTypeName, long sequence) : this(
        $"Unknown event type name alias '{eventTypeName}'. You may need to register this event type through StoreOptions.Events.AddEventType(type)",
        eventTypeName, sequence)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected UnknownEventTypeException(string message, string? eventTypeName, long sequence) : base(message)
    {
        EventTypeName = eventTypeName;
        Sequence = sequence;
    }

    /// <summary>
    ///     Store-wide sequence of the offending event row, or <see cref="UnknownSequence" /> when the
    ///     throw site had no row.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    ///     The unresolvable type name as stored.
    /// </summary>
    public string? EventTypeName { get; }

    public ShardFailureCategory Category => ShardFailureCategory.UnknownEventType;

    // The type never resolved, so no event was ever materialized to read these from.
    Guid? IEventFailureContext.EventId => null;
    Guid? IEventFailureContext.StreamId => null;
    string? IEventFailureContext.StreamKey => null;
    string? IEventFailureContext.TenantId => null;
    long? IEventFailureContext.Version => null;
}
