namespace JasperFx.Events;

/// <summary>
/// Write-side event-store API basics: append events to streams and start new streams.
/// Canonical home for the surface that Marten's <c>IEventOperations</c> and
/// Polecat's write-side event API share.
/// </summary>
/// <remarks>
/// <see cref="IEventStoreOperations"/> extends this contract with the
/// aggregate-handler workflow (FetchForWriting, WriteToAggregate, optimistic /
/// exclusive concurrency, archive, etc.).
/// </remarks>
public interface IEventOperations
{
    /// <summary>
    /// Append events in order to an existing stream by Guid identity.
    /// </summary>
    StreamAction Append(Guid stream, IEnumerable<object> events);

    /// <summary>
    /// Append events in order to an existing stream by Guid identity.
    /// </summary>
    StreamAction Append(Guid stream, params object[] events);

    /// <summary>
    /// Append events in order to an existing stream by string key.
    /// </summary>
    StreamAction Append(string stream, IEnumerable<object> events);

    /// <summary>
    /// Append events in order to an existing stream by string key.
    /// </summary>
    StreamAction Append(string stream, params object[] events);

    /// <summary>
    /// Append events to an existing stream and verify that the maximum event id for the
    /// stream matches the supplied expected version or the transaction is aborted.
    /// </summary>
    StreamAction Append(Guid stream, long expectedVersion, params object[] events);

    /// <summary>
    /// Append events to an existing stream and verify that the maximum event id for the
    /// stream matches the supplied expected version or the transaction is aborted.
    /// </summary>
    StreamAction Append(string stream, long expectedVersion, IEnumerable<object> events);

    /// <summary>
    /// Append events to an existing stream and verify that the maximum event id for the
    /// stream matches the supplied expected version or the transaction is aborted.
    /// </summary>
    StreamAction Append(string stream, long expectedVersion, params object[] events);

    /// <summary>
    /// Create a new event stream based on a user-supplied Guid and append events to it.
    /// Throws when a stream with the same identity already exists.
    /// </summary>
    StreamAction StartStream<TAggregate>(Guid id, params object[] events) where TAggregate : class;

    /// <summary>
    /// Create a new event stream based on a user-supplied Guid and append events to it.
    /// </summary>
    StreamAction StartStream(Type aggregateType, Guid id, IEnumerable<object> events);

    /// <summary>
    /// Create a new event stream based on a user-supplied Guid and append events to it.
    /// </summary>
    StreamAction StartStream(Type aggregateType, Guid id, params object[] events);

    /// <summary>
    /// Create a new event stream based on a user-supplied string key and append events.
    /// </summary>
    StreamAction StartStream<TAggregate>(string streamKey, IEnumerable<object> events) where TAggregate : class;

    /// <summary>
    /// Create a new event stream based on a user-supplied string key and append events.
    /// </summary>
    StreamAction StartStream<TAggregate>(string streamKey, params object[] events) where TAggregate : class;

    /// <summary>
    /// Create a new event stream based on a user-supplied string key and append events.
    /// </summary>
    StreamAction StartStream(Type aggregateType, string streamKey, IEnumerable<object> events);

    /// <summary>
    /// Create a new event stream based on a user-supplied string key and append events.
    /// </summary>
    StreamAction StartStream(Type aggregateType, string streamKey, params object[] events);

    /// <summary>
    /// Create a new event stream based on a user-supplied Guid and append events.
    /// </summary>
    StreamAction StartStream(Guid id, IEnumerable<object> events);

    /// <summary>
    /// Create a new event stream based on a user-supplied Guid and append events.
    /// </summary>
    StreamAction StartStream(Guid id, params object[] events);

    /// <summary>
    /// Create a new event stream based on a user-supplied string key and append events.
    /// </summary>
    StreamAction StartStream(string streamKey, IEnumerable<object> events);

    /// <summary>
    /// Create a new event stream based on a user-supplied string key and append events.
    /// </summary>
    StreamAction StartStream(string streamKey, params object[] events);

    /// <summary>
    /// Create a new event stream, assign a new Guid id, and append events to it.
    /// </summary>
    StreamAction StartStream<TAggregate>(IEnumerable<object> events) where TAggregate : class;

    /// <summary>
    /// Create a new event stream, assign a new Guid id, and append events to it.
    /// </summary>
    StreamAction StartStream<TAggregate>(params object[] events) where TAggregate : class;

    /// <summary>
    /// Create a new event stream, assign a new Guid id, and append events to it.
    /// </summary>
    StreamAction StartStream(Type aggregateType, IEnumerable<object> events);

    /// <summary>
    /// Create a new event stream, assign a new Guid id, and append events to it.
    /// </summary>
    StreamAction StartStream(Type aggregateType, params object[] events);

    /// <summary>
    /// Create a new event stream, assign a new Guid id, and append events to it.
    /// </summary>
    StreamAction StartStream(IEnumerable<object> events);

    /// <summary>
    /// Create a new event stream, assign a new Guid id, and append events to it.
    /// </summary>
    StreamAction StartStream(params object[] events);

    // NOTE: CompactStreamAsync<T>(...) is intentionally not lifted here. Marten and Polecat
    // each ship a public StreamCompactingRequest<T> with product-specific execution
    // (against IDocumentOperations / IDocumentSession). Lifting that data class is a
    // follow-up; until then each product exposes CompactStreamAsync on its own derived
    // interface.
}
