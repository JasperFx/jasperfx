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
/// <remarks>
///     jasperfx#872 added the remediation half of the message. Because plain <c>Append</c> is
///     deliberately start-or-append on every store, the only ways to reach this exception are the
///     optimistic and exclusive overloads (and Marten's mandatory stream type declaration) — so
///     readers routinely mis-read the bare fact as "Append needs StartStream first" and change code
///     that was never wrong. The message now says which calls demand an existing stream.
/// </remarks>
public class NonExistentStreamException : Exception
{
    public NonExistentStreamException(object id)
        : this(
            $"Attempt to append to a nonexistent event stream '{id}'. AppendOptimistic and AppendExclusive require the stream to already exist in this session's tenant; call StartStream first, or use plain Append, which starts the stream when it is missing.",
            id)
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
