namespace JasperFx.Events.Appending;

/// <summary>
///     The exceptions an append refusal turns into. The shared types by default; a store whose callers
///     catch its own subclasses overrides the members for those, the way Fisher's and Polecat's
///     <c>ExistingStreamIdCollisionException</c> and <c>ArchivedStreamException</c> keep their namespaces.
/// </summary>
public class AppendRefusals
{
    /// <summary>
    ///     The shared exception types.
    /// </summary>
    public static AppendRefusals Shared { get; } = new();

    /// <summary>
    ///     A stream was started under an id that is already in use.
    /// </summary>
    public virtual Exception Collision(object id, Type? aggregateType)
        => new ExistingStreamIdCollisionException(id, aggregateType);

    /// <summary>
    ///     Events were appended to an archived stream.
    /// </summary>
    public virtual Exception Archived(object id) => new ArchivedStreamException(id);

    /// <summary>
    ///     The stream is not at the version the unit of work expected.
    /// </summary>
    public virtual Exception UnexpectedVersion(object id, Type? aggregateType, long expected, long actual)
        => new EventStreamUnexpectedMaxEventIdException(id, aggregateType, expected, actual);
}
