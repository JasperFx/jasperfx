using System.Linq.Expressions;
using JasperFx.Events.Tags;

namespace JasperFx.Events;

/// <summary>
/// Full session-level event-store API: read + write + the aggregate-handler workflow
/// (FetchForWriting / WriteToAggregate / optimistic / exclusive concurrency / fetch
/// latest / project latest / tag queries / natural-key fetches).
/// </summary>
/// <remarks>
/// Canonical home of the surface that Marten's <c>IEventStoreOperations</c> and
/// Polecat's combined <c>IEventOperations</c> share. Wolverine codegen targets this
/// contract directly — both products' session types implement it so the aggregate
/// handler frames don't need to be parameterized on a session generic.
///
/// Intentionally excluded from this version of the contract:
/// <list type="bullet">
///   <item><c>StreamLatestJson&lt;T&gt;(...)</c> — Marten-specific JSON-passthrough optimization.</item>
/// </list>
/// </remarks>
public interface IEventStoreOperations : IEventOperations, IQueryEventStore
{
    /// <summary>
    /// Helper to create an event wrapper as part of appending events when custom metadata is needed.
    /// </summary>
    IEvent BuildEvent(object data);

    /// <summary>
    /// Append events to an existing stream and verify that the maximum event id for the stream
    /// matches the supplied expected version or the transaction is aborted.
    /// </summary>
    StreamAction Append(Guid stream, long expectedVersion, IEnumerable<object> events);

    /// <summary>
    /// Create a new event stream with a user-supplied Guid and append events to it.
    /// </summary>
    StreamAction StartStream<TAggregate>(Guid id, IEnumerable<object> events) where TAggregate : class;

    /// <summary>
    /// Append events to an existing stream with an optimistic concurrency check against
    /// the existing version of the stream.
    /// </summary>
    Task AppendOptimistic(string streamKey, CancellationToken token, params object[] events);

    /// <summary>
    /// Append events to an existing stream with an optimistic concurrency check against
    /// the existing version of the stream.
    /// </summary>
    Task AppendOptimistic(string streamKey, params object[] events);

    /// <summary>
    /// Append events to an existing stream with an optimistic concurrency check against
    /// the existing version of the stream.
    /// </summary>
    Task AppendOptimistic(Guid streamId, CancellationToken token, params object[] events);

    /// <summary>
    /// Append events to an existing stream with an optimistic concurrency check against
    /// the existing version of the stream.
    /// </summary>
    Task AppendOptimistic(Guid streamId, params object[] events);

    /// <summary>
    /// Append events to an existing stream with an exclusive lock against the stream until
    /// this session is saved.
    /// </summary>
    Task AppendExclusive(string streamKey, CancellationToken token, params object[] events);

    /// <summary>
    /// Append events to an existing stream with an exclusive lock against the stream until
    /// this session is saved.
    /// </summary>
    Task AppendExclusive(string streamKey, params object[] events);

    /// <summary>
    /// Append events to an existing stream with an exclusive lock against the stream until
    /// this session is saved.
    /// </summary>
    Task AppendExclusive(Guid streamId, CancellationToken token, params object[] events);

    /// <summary>
    /// Append events to an existing stream with an exclusive lock against the stream until
    /// this session is saved.
    /// </summary>
    Task AppendExclusive(Guid streamId, params object[] events);

    /// <summary>
    /// Mark a stream and all of its events as archived.
    /// </summary>
    void ArchiveStream(Guid streamId);

    /// <summary>
    /// Mark a stream and all of its events as archived.
    /// </summary>
    void ArchiveStream(string streamKey);

    /// <summary>
    /// Fetch the projected aggregate T by id with built-in optimistic concurrency checks
    /// starting at the point the aggregate was fetched.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T>(Guid id, CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Fetch the projected aggregate T by id with built-in optimistic concurrency checks
    /// starting at the point the aggregate was fetched.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T>(string key, CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Fetch projected aggregate T by id and expected, current version. Fails immediately
    /// with a concurrency exception if the expectedVersion is stale.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T>(Guid id, long expectedVersion, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Fetch projected aggregate T by id and expected, current version. Fails immediately
    /// with a concurrency exception if the expectedVersion is stale.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T>(string key, long expectedVersion, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Fetch projected aggregate T by id for exclusive writing.
    /// </summary>
    Task<IEventStream<T>> FetchForExclusiveWriting<T>(Guid id, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Fetch projected aggregate T by id for exclusive writing.
    /// </summary>
    Task<IEventStream<T>> FetchForExclusiveWriting<T>(string key, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Conditionally write to an event stream for the current version of the aggregate of type T.
    /// Automatically persists the entire session.
    /// </summary>
    Task WriteToAggregate<T>(Guid id, Action<IEventStream<T>> writing, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Conditionally write to an event stream for the current version of the aggregate of type T.
    /// Automatically persists the entire session.
    /// </summary>
    Task WriteToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Conditionally write to an event stream for the current version of the aggregate of type T.
    /// Automatically persists the entire session.
    /// </summary>
    Task WriteToAggregate<T>(string id, Action<IEventStream<T>> writing, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Conditionally write to an event stream for the current version of the aggregate of type T.
    /// Automatically persists the entire session.
    /// </summary>
    Task WriteToAggregate<T>(string id, Func<IEventStream<T>, Task> writing, CancellationToken cancellation = default)
        where T : class;

    /// <summary>
    /// Conditionally write to an event stream at a specific expected starting version.
    /// </summary>
    Task WriteToAggregate<T>(Guid id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Conditionally write to an event stream at a specific expected starting version.
    /// </summary>
    Task WriteToAggregate<T>(Guid id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Conditionally write to an event stream at a specific expected starting version.
    /// </summary>
    Task WriteToAggregate<T>(string id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Conditionally write to an event stream at a specific expected starting version.
    /// </summary>
    Task WriteToAggregate<T>(string id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Write exclusively to the stream for aggregate of type T. May time out if a lock
    /// cannot be attained on the stream in time.
    /// </summary>
    Task WriteExclusivelyToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Write exclusively to the stream for aggregate of type T. May time out if a lock
    /// cannot be attained on the stream in time.
    /// </summary>
    Task WriteExclusivelyToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Write exclusively to the stream for aggregate of type T. May time out if a lock
    /// cannot be attained on the stream in time.
    /// </summary>
    Task WriteExclusivelyToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Write exclusively to the stream for aggregate of type T. May time out if a lock
    /// cannot be attained on the stream in time.
    /// </summary>
    Task WriteExclusivelyToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Advanced usage: register an operation with the current session to overwrite the
    /// data or headers of an existing event.
    /// </summary>
    void OverwriteEvent(IEvent e);

    /// <summary>
    /// Fetch the projected aggregate T by id. Functions regardless of the projection
    /// lifecycle — a lightweight, read-only counterpart to FetchForWriting.
    /// </summary>
    ValueTask<T?> FetchLatest<T>(Guid id, CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Fetch the projected aggregate T by id. Functions regardless of the projection
    /// lifecycle — a lightweight, read-only counterpart to FetchForWriting.
    /// </summary>
    ValueTask<T?> FetchLatest<T>(string id, CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Fetch the projected aggregate T by id, including any events appended in this
    /// session that have not yet been committed. For inline projections, the updated
    /// document is also stored in the session so it will be persisted on the next
    /// SaveChangesAsync call.
    /// </summary>
    ValueTask<T?> ProjectLatest<T>(Guid id, CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Fetch the projected aggregate T by id, including any events appended in this
    /// session that have not yet been committed.
    /// </summary>
    ValueTask<T?> ProjectLatest<T>(string id, CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Replace event data at a specified spot in the event store without changing stream
    /// identity or version. Replaces all header information with empty. Originally
    /// intended for stream compacting.
    /// </summary>
    Guid CompletelyReplaceEvent<T>(long sequence, T eventBody) where T : class;

    /// <summary>
    /// Retroactively assign a tag to all events matching the given LINQ predicate. The
    /// tag's type must be registered via the event registry. The operation is queued
    /// and applied at SaveChangesAsync time.
    /// </summary>
    void AssignTagWhere(Expression<Func<IEvent, bool>> expression, object tag);

    /// <summary>
    /// Lightweight existence check: are there any events matching the given tag query?
    /// Useful for DCB guard clauses.
    /// </summary>
    Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken cancellation = default);

    /// <summary>
    /// Query events by their tags. Returns events matching any of the OR'd conditions
    /// in the query, ordered by sequence id.
    /// </summary>
    Task<IReadOnlyList<IEvent>> QueryByTagsAsync(EventTagQuery query, CancellationToken cancellation = default);

    /// <summary>
    /// Query events by tag and aggregate them into type T using a live fold.
    /// </summary>
    Task<T?> AggregateByTagsAsync<T>(EventTagQuery query, CancellationToken cancellation = default) where T : class;

    /// <summary>
    /// Fetch every event matching the tag query, aggregate them into <typeparamref name="T"/>,
    /// and return a writable <see cref="IEventBoundary{T}"/> that enforces Dynamic
    /// Consistency Boundary (DCB) checking. Additional events appended via the
    /// boundary are routed to the appropriate stream(s) by tag; at
    /// <c>SaveChangesAsync()</c> time the consuming product asserts that no new
    /// events matching the tag query have been written past
    /// <see cref="IEventBoundary{T}.LastSeenSequence"/>, throwing a product-specific
    /// DCB concurrency exception if the boundary has shifted.
    /// </summary>
    Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query,
        CancellationToken cancellation = default) where T : class;

    // Natural-key (strong-typed-id) fetch overloads ---------------------------------------------

    /// <summary>
    /// Fetch projected aggregate T by a natural key or any registered identifier type
    /// with built-in optimistic concurrency checks.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull;

    /// <summary>
    /// Fetch projected aggregate T by a natural key for exclusive writing with row-level locking.
    /// </summary>
    Task<IEventStream<T>> FetchForExclusiveWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull;

    /// <summary>
    /// Fetch the projected aggregate T by a natural key or any registered identifier
    /// type. Lightweight, read-only counterpart to FetchForWriting.
    /// </summary>
    ValueTask<T?> FetchLatest<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull;
}
