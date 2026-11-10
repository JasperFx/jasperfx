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
///     Marten's shape. <see cref="StreamIds" /> is Fisher's (jasperfx#1172): SQLite takes one writer
///     per file, so every stream in a unit of work loses the lock together. Stores throw this type
///     directly; a store whose message diverges subclasses through the protected constructor.
/// </remarks>
public class StreamLockedException : ConcurrencyException
{
    private const string RemedyHint = "retry the operation, or reduce the number of concurrent writers";

    public StreamLockedException(object streamId, Exception? innerException)
        : this(buildMessage([streamId]), streamId, innerException)
    {
    }

    /// <summary>
    ///     For a store whose lock covers more than one stream at a time, like Fisher's SQLite
    ///     file lock. <see cref="StreamId" /> and <see cref="ConcurrencyException.Id" /> carry the
    ///     first id, so code written for the single-stream shape still reads the same.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="streamIds" /> is empty: a locked-stream failure that names no stream
    ///     is a worse answer than the database's own exception.
    /// </exception>
    public StreamLockedException(IReadOnlyList<object> streamIds, Exception? innerException)
        : this(buildMessage(streamIds), firstOf(streamIds), innerException)
    {
        StreamIds = streamIds;
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected StreamLockedException(string message, object streamId, Exception? innerException)
        : base(message, innerException!)
    {
        StreamId = streamId;
        StreamIds = [streamId];
        Id = streamId;
    }

    public object StreamId { get; }

    /// <summary>
    ///     Every stream that lost the lock, in the order the store queued them. The single-stream
    ///     constructors populate this with just <see cref="StreamId" />, so it is never empty.
    /// </summary>
    public IReadOnlyList<object> StreamIds { get; }

    private static object firstOf(IReadOnlyList<object> streamIds)
    {
        ArgumentNullException.ThrowIfNull(streamIds);
        if (streamIds.Count == 0)
        {
            throw new ArgumentException("At least one stream id is required", nameof(streamIds));
        }

        return streamIds[0];
    }

    private static string buildMessage(IReadOnlyList<object> streamIds)
    {
        var subject = streamIds.Count == 1
            ? $"Stream '{streamIds[0]}'"
            : $"Streams '{string.Join("', '", streamIds)}'";

        return $"{subject} may be locked for updates; {RemedyHint}";
    }
}
