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
public class ExistingStreamIdCollisionException : Exception
{
    public ExistingStreamIdCollisionException(object id) : this(id, null)
    {
    }

    public ExistingStreamIdCollisionException(object id, Type? aggregateType)
        : this($"Stream with id '{id}' already exists.", id, aggregateType)
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
