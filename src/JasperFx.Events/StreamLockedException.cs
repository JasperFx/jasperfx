namespace JasperFx.Events;

/// <summary>
///     Thrown when a stream cannot be locked for exclusive access, typically because another
///     transaction holds a lock on the stream row.
///     Derives from <see cref="ConcurrencyException" /> as of 3.0: it is the same user-facing failure
///     (a conflict worth retrying), so <c>catch (ConcurrencyException)</c> also catches lock timeouts.
///     <see cref="ConcurrencyException.Id" /> carries the stream id, and <see cref="ConcurrencyException.DocType" />
///     is null.
/// </summary>
/// <remarks>
///     Lifted from the identically-messaged exceptions in Marten and Polecat. The
///     <see cref="StreamId" /> property and the nullable inner exception are Polecat's supersets of
///     Marten's shape. Stores subclass or type-forward to this.
/// </remarks>
public class StreamLockedException : ConcurrencyException
{
    public StreamLockedException(object streamId, Exception? innerException)
        : this($"Stream '{streamId}' may be locked for updates", streamId, innerException)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected StreamLockedException(string message, object streamId, Exception? innerException)
        : base(message, innerException!)
    {
        StreamId = streamId;
        Id = streamId;
    }

    public object StreamId { get; }
}
