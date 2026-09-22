namespace JasperFx.Events;

/// <summary>
///     Thrown when attempting to start a new event stream with an id that already exists in the
///     database.
/// </summary>
/// <remarks>
///     Lifted from the per-store exceptions of the same name. The <see cref="AggregateType" /> is
///     Marten's addition and is nullable here because Polecat and Fisher throw without one; the
///     message is Polecat's/Fisher's, and Marten's diverged wording
///     (<c>Stream #... already exists in the database</c>) stays expressible through the protected
///     message-overriding constructor. Stores subclass or type-forward to this.
/// </remarks>
/// <remarks>
///     jasperfx#872 added the remediation half of the message. The fact alone ("already exists")
///     leaves the reader to work out that <c>Append</c> is start-or-append on every store and that
///     <c>StartStream</c> is the only call that insists on a new id, which is exactly the confusion
///     this exception tends to produce.
/// </remarks>
public class ExistingStreamIdCollisionException : Exception
{
    public ExistingStreamIdCollisionException(object id) : this(id, null)
    {
    }

    public ExistingStreamIdCollisionException(object id, Type? aggregateType)
        : this(
            $"Stream with id '{id}' already exists. StartStream requires a new id; to add events to an existing stream use Append (which starts the stream if it is missing) or FetchForWriting, and make create commands idempotent on the stream id.",
            id, aggregateType)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected ExistingStreamIdCollisionException(string message, object id, Type? aggregateType) : base(message)
    {
        Id = id;
        AggregateType = aggregateType;
    }

    public object Id { get; }

    /// <summary>
    ///     The aggregate type the colliding stream was being started for, when the throw site knew it.
    /// </summary>
    public Type? AggregateType { get; }
}
