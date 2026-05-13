namespace JasperFx.Events;

/// <summary>
/// How a FetchForWriting / aggregate-handler workflow should enforce concurrent access
/// to an event stream while a handler is running.
/// </summary>
/// <remarks>
/// Canonical home is JasperFx.Events so that Wolverine.Marten, Wolverine.Polecat, and any
/// future event-store integration share one enum rather than each defining a private copy.
/// </remarks>
public enum ConcurrencyStyle
{
    /// <summary>
    /// Check for concurrency violations optimistically at the point of committing the updated data.
    /// </summary>
    Optimistic,

    /// <summary>
    /// Try to attain an exclusive lock on the data behind the current aggregate.
    /// </summary>
    Exclusive
}
