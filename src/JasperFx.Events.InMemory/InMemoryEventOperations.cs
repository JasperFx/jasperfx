using System.Linq.Expressions;
using JasperFx.Core;
using JasperFx.Events.Tags;

namespace JasperFx.Events.InMemory;

/// <summary>
/// <c>session.Events</c> on the in-memory prototyping store (jasperfx#964). Appends are tracked on the
/// session and land in its all-or-nothing commit; reads see what has been committed.
/// </summary>
/// <remarks>
/// Out of scope for a prototyping store, and refused with a <see cref="NotSupportedException"/> that says
/// so: archiving, compaction, rewriting events, and the tag (DCB) queries.
/// </remarks>
public partial class InMemoryEventOperations : EventOperationsBase, IEventStoreOperations, IReadOnlyEventStore
{
    private readonly InMemoryDocumentSession _session;
    private readonly Dictionary<object, StreamAction> _streams = new();

    internal InMemoryEventOperations(InMemoryDocumentSession session)
    {
        _session = session;
    }

    private InMemoryDocumentStore Store => _session.OwningStore;
    private InMemoryEventRegistry Registry => _session.OwningStore.Events;
    private bool IsGuidIdentity => Registry.StreamIdentity == StreamIdentity.AsGuid;

    internal IReadOnlyList<StreamAction> PendingStreams => _streams.Values.ToList();

    internal void ClearPendingStreams() => _streams.Clear();

    /// <inheritdoc />
    public IEvent BuildEvent(object data) => Registry.BuildEvent(data);

    // ---- The seams of EventOperationsBase ----

    /// <inheritdoc />
    protected override StreamIdentity StreamIdentity => Registry.StreamIdentity;

    /// <inheritdoc />
    protected override StreamAction StartStreamCore(Type? aggregateType, Guid id, object[] events)
    {
        var stream = track(id, StreamAction.Start(Registry, id, events));
        stream.AggregateType = aggregateType;
        return stream;
    }

    /// <inheritdoc />
    protected override StreamAction StartStreamCore(Type? aggregateType, string streamKey, object[] events)
    {
        var stream = track(streamKey, StreamAction.Start(Registry, streamKey, events));
        stream.AggregateType = aggregateType;
        return stream;
    }

    /// <inheritdoc />
    protected override StreamAction AppendCore(Guid stream, object[] events)
        => appendTo(stream, () => StreamAction.Append(Registry, stream, events), events);

    /// <inheritdoc />
    protected override StreamAction AppendCore(string stream, object[] events)
        => appendTo(stream, () => StreamAction.Append(Registry, stream, events), events);

    // There is no lock to take on an in-memory stream, so "exclusive" is the optimistic guard -- the
    // same documented choice Fisher makes for SQLite.

    /// <inheritdoc />
    protected override Task<long> ReadStreamVersionCore<TId>(TId stream, bool exclusive, CancellationToken token)
        => Task.FromResult(requireVersion(stream));

    /// <inheritdoc />
    protected override Task<IEventStream<T>> FetchForWritingCore<T, TId>(TId id, long? expectedVersion, bool exclusive,
        CancellationToken cancellation)
        => fetchForWritingAsync<T>(id, expectedVersion, cancellation);

    /// <inheritdoc />
    protected override Task SaveChangesCore(CancellationToken cancellation) => _session.SaveChangesAsync(cancellation);

    // ---- Reading ----

    /// <inheritdoc />
    public Task<IReadOnlyList<IEvent>> FetchStreamAsync(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default)
    {
        assertGuidIdentity();
        return Task.FromResult(Store.EventsFor(streamId, version, timestamp, fromVersion));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IEvent>> FetchStreamAsync(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default)
    {
        assertStringIdentity();
        return Task.FromResult(Store.EventsFor(streamKey, version, timestamp, fromVersion));
    }

    /// <inheritdoc />
    public Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken token = default)
    {
        assertGuidIdentity();
        return Task.FromResult(Store.StreamStateFor(streamId));
    }

    /// <inheritdoc />
    public Task<StreamState?> FetchStreamStateAsync(string streamKey, CancellationToken token = default)
    {
        assertStringIdentity();
        return Task.FromResult(Store.StreamStateFor(streamKey));
    }

    /// <inheritdoc />
    public Task<IEvent<T>?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class
        => Task.FromResult(Store.EventById(id) as IEvent<T>);

    /// <inheritdoc />
    public Task<IEvent?> LoadAsync(Guid id, CancellationToken token = default)
        => Task.FromResult(Store.EventById(id));

    /// <summary>
    /// The cross-stream query (jasperfx#985). Every filter but the tag (DCB) ones, which are refused rather than
    /// ignored.
    /// </summary>
    public Task<PagedEvents> QueryEventsAsync(EventQuery query, CancellationToken token = default)
        => Task.FromResult(Store.QueryEvents(query));

    /// <summary>
    /// Every stream, queried with LINQ-to-objects over a snapshot (jasperfx#985). Nothing is ever archived or
    /// compacted on the prototyping store.
    /// </summary>
    public IQueryable<StreamState> QueryStreamStates(string? tenantId = null)
        => InMemoryDocumentQueryable<StreamState>.Wrap(Store.StreamStates(tenantId).AsQueryable());

    /// <summary>
    /// The bodies of every event stored as exactly <typeparamref name="T"/>, queried with LINQ-to-objects over a
    /// snapshot (jasperfx#1073).
    /// </summary>
    public IQueryable<T> QueryEventData<T>(string? tenantId = null) where T : class
        => InMemoryDocumentQueryable<T>.Wrap(Store.EventData<T>(tenantId).AsQueryable());

    // ---- Out of scope for the prototyping store ----

    /// <inheritdoc />
    public void ArchiveStream(Guid streamId) => throw InMemoryEventRegistry.NotSupported("Archiving a stream");

    /// <inheritdoc />
    public void ArchiveStream(string streamKey) => throw InMemoryEventRegistry.NotSupported("Archiving a stream");

    /// <inheritdoc />
    public void OverwriteEvent(IEvent e) => throw InMemoryEventRegistry.NotSupported("Overwriting an event");

    /// <inheritdoc />
    public Guid CompletelyReplaceEvent<T>(long sequence, T eventBody) where T : class
        => throw InMemoryEventRegistry.NotSupported("Replacing an event");

    /// <inheritdoc />
    public void AssignTagWhere(Expression<Func<IEvent, bool>> expression, object tag)
        => throw InMemoryEventRegistry.NotSupported("Event tags (DCB)");

    /// <inheritdoc />
    public Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken cancellation = default)
        => throw InMemoryEventRegistry.NotSupported("Event tag queries (DCB)");

    /// <inheritdoc />
    public Task<IReadOnlyList<IEvent>> QueryByTagsAsync(EventTagQuery query, CancellationToken cancellation = default)
        => throw InMemoryEventRegistry.NotSupported("Event tag queries (DCB)");

    /// <inheritdoc />
    public Task<T?> AggregateByTagsAsync<T>(EventTagQuery query, CancellationToken cancellation = default)
        where T : class
        => throw InMemoryEventRegistry.NotSupported("Event tag queries (DCB)");

    /// <inheritdoc />
    public Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query,
        CancellationToken cancellation = default) where T : class
        => throw InMemoryEventRegistry.NotSupported("Event tag queries (DCB)");

    // ---- Tracking ----

    private StreamAction appendTo(object key, Func<StreamAction> create, object[] events)
    {
        if (_streams.TryGetValue(key, out var existing))
        {
            existing.AddEvents(events.Select(Registry.BuildEvent).ToArray());
            return existing;
        }

        return track(key, create());
    }

    private StreamAction track(object key, StreamAction stream)
    {
        stream.TenantId = _session.TenantId;
        _streams[key] = stream;
        return stream;
    }

    private long requireVersion(object streamKey)
        => Store.CurrentVersion(streamKey) ?? throw new NonExistentStreamException(streamKey);

    private void assertGuidIdentity()
    {
        if (!IsGuidIdentity)
        {
            throw new InvalidOperationException(
                "This store is configured for string stream identity (StreamIdentity.AsString); " +
                "use the string stream key overloads.");
        }
    }

    private void assertStringIdentity()
    {
        if (IsGuidIdentity)
        {
            throw new InvalidOperationException(
                "This store is configured for Guid stream identity (StreamIdentity.AsGuid); " +
                "use the Guid stream id overloads.");
        }
    }
}
