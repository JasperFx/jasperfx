namespace JasperFx.Events;

/// <summary>
///     Thrown when appending to a stream that does not exist, from an append that had to read the
///     stream's current state first — the optimistic and exclusive append paths. A plain
///     <c>Append</c> does not throw this; it queues the events and lets the write fail at save time,
///     because there is nothing to read up front.
/// </summary>
/// <remarks>
///     Lifted from the identically-shaped exceptions in Marten, Polecat and Fisher. Stores subclass
///     or type-forward to this.
/// </remarks>
public class NonExistentStreamException : Exception
{
    public NonExistentStreamException(object id)
        : this($"Attempt to append to a nonexistent event stream '{id}'", id)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected NonExistentStreamException(string message, object id) : base(message)
    {
        Id = id;
    }

    public object Id { get; }
}
