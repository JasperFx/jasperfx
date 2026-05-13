namespace JasperFx.Events;

/// <summary>
/// Internal marker that lets the event-sourcing infrastructure advance the expected
/// starting version on a stream handle without exposing the concrete type.
/// </summary>
internal interface IEventStream
{
    void TryFastForwardVersion();
}

/// <summary>
/// A writable handle to an event stream and its currently-projected aggregate.
/// Returned by FetchForWriting / FetchForExclusiveWriting on a session-bound event store.
/// </summary>
/// <remarks>
/// Canonical home is JasperFx.Events. Marten and Polecat each expose product-specific
/// re-exports / inheriting interfaces, but the shape is shared so that Wolverine codegen
/// can target a single contract regardless of which event store is backing the handler.
/// </remarks>
public interface IEventStream<out T> where T : notnull
{
    /// <summary>
    /// The projected aggregate state at the moment the stream was fetched, or null
    /// if the stream does not yet exist.
    /// </summary>
    T? Aggregate { get; }

    /// <summary>
    /// The version of the stream at the moment it was fetched. Null when the stream
    /// did not exist at fetch time.
    /// </summary>
    long? StartingVersion { get; }

    /// <summary>
    /// The expected version after the events queued on this handle are persisted —
    /// equivalent to <see cref="StartingVersion"/> + the count of pending events.
    /// Null when <see cref="StartingVersion"/> is null.
    /// </summary>
    long? CurrentVersion { get; }

    /// <summary>
    /// Cancellation token bound to the fetch operation that produced this handle.
    /// Useful when async work needs to honor the same cancellation as the load.
    /// </summary>
    CancellationToken Cancellation { get; }

    /// <summary>
    /// The Guid identity of the stream, or Guid.Empty when the stream is keyed by string.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// The string key of the stream, or null when the stream is keyed by Guid.
    /// </summary>
    string? Key { get; }

    /// <summary>
    /// Events that have been appended to this handle and will be persisted when the
    /// session is saved.
    /// </summary>
    IReadOnlyList<IEvent> Events { get; }

    /// <summary>
    /// Append a single event to the stream.
    /// </summary>
    void AppendOne(object @event);

    /// <summary>
    /// Append multiple events to the stream.
    /// </summary>
    void AppendMany(params object[] events);

    /// <summary>
    /// Append multiple events to the stream.
    /// </summary>
    void AppendMany(IEnumerable<object> events);

    /// <summary>
    /// When true, the underlying event store will enforce an optimistic concurrency
    /// check on this stream at save time even if no events are appended. Useful when
    /// the handler decides not to emit events but still needs to assert the stream
    /// version has not advanced since it was fetched.
    /// </summary>
    bool AlwaysEnforceConsistency { get; set; }

    /// <summary>
    /// Advance the expected starting version for optimistic concurrency checks to
    /// the current version, so a single stream handle can be reused across multiple
    /// units of work. Intended for very specific advanced scenarios.
    /// </summary>
    void TryFastForwardVersion();
}
