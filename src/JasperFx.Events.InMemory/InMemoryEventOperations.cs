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
public partial class InMemoryEventOperations : IEventStoreOperations, IReadOnlyEventStore
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

    // ---- StartStream ----

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => StartStream(typeof(TAggregate), id, events);

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(Guid id, IEnumerable<object> events) where TAggregate : class
        => StartStream(typeof(TAggregate), id, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, Guid id, IEnumerable<object> events)
        => StartStream(aggregateType, id, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, Guid id, params object[] events)
    {
        var stream = StartStream(id, events);
        stream.AggregateType = aggregateType;
        return stream;
    }

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(string streamKey, IEnumerable<object> events) where TAggregate : class
        => StartStream(typeof(TAggregate), streamKey, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(string streamKey, params object[] events) where TAggregate : class
        => StartStream(typeof(TAggregate), streamKey, events);

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, string streamKey, IEnumerable<object> events)
        => StartStream(aggregateType, streamKey, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, string streamKey, params object[] events)
    {
        var stream = StartStream(streamKey, events);
        stream.AggregateType = aggregateType;
        return stream;
    }

    /// <inheritdoc />
    public StreamAction StartStream(Guid id, IEnumerable<object> events) => StartStream(id, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Guid id, params object[] events)
    {
        assertGuidIdentity();
        return track(id, StreamAction.Start(Registry, id, events));
    }

    /// <inheritdoc />
    public StreamAction StartStream(string streamKey, IEnumerable<object> events)
        => StartStream(streamKey, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(string streamKey, params object[] events)
    {
        assertStringIdentity();
        return track(streamKey, StreamAction.Start(Registry, streamKey, events));
    }

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(IEnumerable<object> events) where TAggregate : class
        => StartStream(typeof(TAggregate), events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(params object[] events) where TAggregate : class
        => StartStream(typeof(TAggregate), events);

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, IEnumerable<object> events)
        => StartStream(aggregateType, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, params object[] events)
    {
        var stream = StartStream(events);
        stream.AggregateType = aggregateType;
        return stream;
    }

    /// <inheritdoc />
    public StreamAction StartStream(IEnumerable<object> events) => StartStream(events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(params object[] events)
        => IsGuidIdentity
            ? StartStream(CombGuidIdGeneration.NewGuid(), events)
            : StartStream(Guid.NewGuid().ToString(), events);

    // ---- Append ----

    /// <inheritdoc />
    public StreamAction Append(Guid stream, IEnumerable<object> events) => Append(stream, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(Guid stream, params object[] events)
    {
        assertGuidIdentity();
        return appendTo(stream, () => StreamAction.Append(Registry, stream, events), events);
    }

    /// <inheritdoc />
    public StreamAction Append(string stream, IEnumerable<object> events) => Append(stream, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(string stream, params object[] events)
    {
        assertStringIdentity();
        return appendTo(stream, () => StreamAction.Append(Registry, stream, events), events);
    }

    /// <inheritdoc />
    public StreamAction Append(Guid stream, long expectedVersion, IEnumerable<object> events)
        => Append(stream, expectedVersion, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(Guid stream, long expectedVersion, params object[] events)
    {
        var action = Append(stream, events);
        action.ExpectedVersionOnServer = expectedVersion;
        return action;
    }

    /// <inheritdoc />
    public StreamAction Append(string stream, long expectedVersion, IEnumerable<object> events)
        => Append(stream, expectedVersion, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(string stream, long expectedVersion, params object[] events)
    {
        var action = Append(stream, events);
        action.ExpectedVersionOnServer = expectedVersion;
        return action;
    }

    /// <inheritdoc />
    public Task AppendOptimistic(Guid streamId, CancellationToken token, params object[] events)
    {
        assertGuidIdentity();
        Append(streamId, events).ExpectedVersionOnServer = requireVersion(streamId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AppendOptimistic(Guid streamId, params object[] events)
        => AppendOptimistic(streamId, CancellationToken.None, events);

    /// <inheritdoc />
    public Task AppendOptimistic(string streamKey, CancellationToken token, params object[] events)
    {
        assertStringIdentity();
        Append(streamKey, events).ExpectedVersionOnServer = requireVersion(streamKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AppendOptimistic(string streamKey, params object[] events)
        => AppendOptimistic(streamKey, CancellationToken.None, events);

    // There is no lock to take on an in-memory stream, so "exclusive" is the optimistic guard -- the
    // same documented choice Fisher makes for SQLite.

    /// <inheritdoc />
    public Task AppendExclusive(Guid streamId, CancellationToken token, params object[] events)
        => AppendOptimistic(streamId, token, events);

    /// <inheritdoc />
    public Task AppendExclusive(Guid streamId, params object[] events)
        => AppendOptimistic(streamId, CancellationToken.None, events);

    /// <inheritdoc />
    public Task AppendExclusive(string streamKey, CancellationToken token, params object[] events)
        => AppendOptimistic(streamKey, token, events);

    /// <inheritdoc />
    public Task AppendExclusive(string streamKey, params object[] events)
        => AppendOptimistic(streamKey, CancellationToken.None, events);

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
