using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory;

/// <summary>
/// Live aggregation on the in-memory prototyping store (jasperfx#964): folding a stream into its
/// aggregate with the source-generated evolver, and fetching an aggregate for writing.
/// </summary>
/// <remarks>
/// Conventional <c>Apply</c> / <c>Create</c> methods are dispatched by the compile-time
/// JasperFx.Events.SourceGenerator, which the application has to reference; there is no runtime fallback.
/// </remarks>
public partial class InMemoryEventOperations
{
    internal bool IsTracking(StreamAction stream)
        => _streams.TryGetValue(Store.StreamKeyFor(stream), out var tracked) && ReferenceEquals(tracked, stream);

    internal void TrackFetched(StreamAction stream) => _streams[Store.StreamKeyFor(stream)] = stream;

    // ---- Aggregating ----

    /// <inheritdoc />
    public Task<T?> AggregateStreamAsync<T>(Guid streamId, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class
    {
        assertGuidIdentity();
        return aggregateStreamAsync(streamId, version, timestamp, state, fromVersion, token);
    }

    /// <inheritdoc />
    public Task<T?> AggregateStreamAsync<T>(string streamKey, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class
    {
        assertStringIdentity();
        return aggregateStreamAsync(streamKey, version, timestamp, state, fromVersion, token);
    }

    /// <inheritdoc />
    public Task<T?> AggregateStreamToLastKnownAsync<T>(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, CancellationToken token = default) where T : class
    {
        assertGuidIdentity();
        return aggregateStreamToLastKnownAsync<T>(streamId, version, timestamp, token);
    }

    /// <inheritdoc />
    public Task<T?> AggregateStreamToLastKnownAsync<T>(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, CancellationToken token = default) where T : class
    {
        assertStringIdentity();
        return aggregateStreamToLastKnownAsync<T>(streamKey, version, timestamp, token);
    }

    private async Task<T?> aggregateStreamAsync<T>(object streamId, long version, DateTimeOffset? timestamp,
        T? state, long fromVersion, CancellationToken token) where T : class
    {
        var events = Store.EventsFor(streamId, version, timestamp, fromVersion);
        if (events.Count == 0) return state;

        var aggregate = await Store.Projections.AggregatorFor<T>()
            .BuildAsync(events, _session, state, token).ConfigureAwait(false);

        if (aggregate is not null) InMemoryAggregateIdentity.TrySetIdentity(aggregate, streamId);
        return aggregate;
    }

    // The last version that folds to a non-null aggregate -- a stream whose latest event deletes the
    // aggregate still answers with what it was just before.
    private async Task<T?> aggregateStreamToLastKnownAsync<T>(object streamId, long version,
        DateTimeOffset? timestamp, CancellationToken token) where T : class
    {
        var events = Store.EventsFor(streamId, version, timestamp, 0);
        var aggregator = Store.Projections.AggregatorFor<T>();

        for (var count = events.Count; count > 0; count--)
        {
            var aggregate = await aggregator.BuildAsync(events.Take(count).ToList(), _session, null, token)
                .ConfigureAwait(false);

            if (aggregate is not null)
            {
                InMemoryAggregateIdentity.TrySetIdentity(aggregate, streamId);
                return aggregate;
            }
        }

        return null;
    }

    // ---- Fetching for writing ----

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(Guid id, CancellationToken cancellation = default) where T : class
    {
        assertGuidIdentity();
        return fetchForWritingAsync<T>(id, null, cancellation);
    }

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(string key, CancellationToken cancellation = default) where T : class
    {
        assertStringIdentity();
        return fetchForWritingAsync<T>(key, null, cancellation);
    }

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(Guid id, long expectedVersion, CancellationToken cancellation = default)
        where T : class
    {
        assertGuidIdentity();
        return fetchForWritingAsync<T>(id, expectedVersion, cancellation);
    }

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(string key, long expectedVersion, CancellationToken cancellation = default)
        where T : class
    {
        assertStringIdentity();
        return fetchForWritingAsync<T>(key, expectedVersion, cancellation);
    }

    // There is no lock to take on an in-memory stream: "exclusive" is the optimistic guard.

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForExclusiveWriting<T>(Guid id, CancellationToken cancellation = default)
        where T : class
        => FetchForWriting<T>(id, cancellation);

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForExclusiveWriting<T>(string key, CancellationToken cancellation = default)
        where T : class
        => FetchForWriting<T>(key, cancellation);

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => unwrap<T, TId>(id) switch
        {
            Guid guid => FetchForWriting<T>(guid, cancellation),
            var key => FetchForWriting<T>((string)key, cancellation)
        };

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForExclusiveWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => FetchForWriting<T, TId>(id, cancellation);

    private async Task<IEventStream<T>> fetchForWritingAsync<T>(object streamId, long? expectedVersion,
        CancellationToken token) where T : class
    {
        var version = Store.CurrentVersion(streamId);

        if (expectedVersion.HasValue && (version ?? 0) != expectedVersion.Value)
        {
            throw new EventStreamUnexpectedMaxEventIdException(streamId, typeof(T), expectedVersion.Value,
                version ?? 0);
        }

        T? aggregate = null;
        if (version > 0)
        {
            aggregate = await aggregateStreamAsync<T>(streamId, version.Value, null, null, 0, token)
                .ConfigureAwait(false);
        }

        return new InMemoryEventStream<T>(this, trackForWriting(streamId, version), aggregate, token);
    }

    private StreamAction trackForWriting(object streamId, long? version)
    {
        if (_streams.TryGetValue(streamId, out var existing))
        {
            existing.ExpectedVersionOnServer ??= version ?? 0;
            return existing;
        }

        var actionType = version.HasValue ? StreamActionType.Append : StreamActionType.Start;
        var action = streamId is Guid guid
            ? new StreamAction(guid, actionType)
            : new StreamAction((string)streamId, actionType);

        action.ExpectedVersionOnServer = version ?? 0;
        return track(streamId, action);
    }

    // ---- WriteToAggregate: fetch, run the caller's decision, commit ----

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(Guid id, Action<IEventStream<T>> writing, CancellationToken cancellation = default)
        where T : class
    {
        writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing, CancellationToken cancellation = default)
        where T : class
    {
        await writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(string id, Action<IEventStream<T>> writing, CancellationToken cancellation = default)
        where T : class
    {
        writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(string id, Func<IEventStream<T>, Task> writing, CancellationToken cancellation = default)
        where T : class
    {
        await writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(Guid id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
    {
        writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(Guid id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
    {
        await writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false))
            .ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(string id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
    {
        writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteToAggregate<T>(string id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
    {
        await writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false))
            .ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    // ---- Latest ----

    /// <inheritdoc />
    public async ValueTask<T?> FetchLatest<T>(Guid id, CancellationToken cancellation = default) where T : class
    {
        assertGuidIdentity();
        return await fetchLatestAsync<T>(id, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<T?> FetchLatest<T>(string id, CancellationToken cancellation = default) where T : class
    {
        assertStringIdentity();
        return await fetchLatestAsync<T>(id, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<T?> FetchLatest<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => unwrap<T, TId>(id) switch
        {
            Guid guid => FetchLatest<T>(guid, cancellation),
            var key => FetchLatest<T>((string)key, cancellation)
        };

    // An aggregate kept current by an Inline snapshot is read as the stored document; anything else is
    // folded from its events.
    private async ValueTask<T?> fetchLatestAsync<T>(object streamId, CancellationToken token) where T : class
    {
        if (Store.Projections.TryFindAggregate(typeof(T), out var projection)
            && projection.Lifecycle == ProjectionLifecycle.Inline)
        {
            return await _session.LoadAsync<T>(streamId, token).ConfigureAwait(false);
        }

        return await aggregateStreamAsync<T>(streamId, 0, null, null, 0, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<T?> ProjectLatest<T>(Guid id, CancellationToken cancellation = default) where T : class
    {
        assertGuidIdentity();
        return projectLatestAsync<T>(id, cancellation);
    }

    /// <inheritdoc />
    public ValueTask<T?> ProjectLatest<T>(string id, CancellationToken cancellation = default) where T : class
    {
        assertStringIdentity();
        return projectLatestAsync<T>(id, cancellation);
    }

    // The committed fold, with this session's not-yet-committed events for the stream folded on top.
    private async ValueTask<T?> projectLatestAsync<T>(object streamId, CancellationToken token) where T : class
    {
        var committed = await aggregateStreamAsync<T>(streamId, 0, null, null, 0, token).ConfigureAwait(false);

        if (!_streams.TryGetValue(streamId, out var pending) || pending.Events.Count == 0) return committed;

        var projected = await Store.Projections.AggregatorFor<T>()
            .BuildAsync(pending.Events, _session, committed, token).ConfigureAwait(false);

        if (projected is not null) InMemoryAggregateIdentity.TrySetIdentity(projected, streamId);
        return projected;
    }

    private object unwrap<T, TId>(TId id) where TId : notnull
    {
        if (InMemoryAggregateIdentity.TryUnwrap(id, out var streamId)
            && (streamId is Guid ? IsGuidIdentity : !IsGuidIdentity))
        {
            return streamId;
        }

        throw new NotSupportedException(
            $"The in-memory prototyping store cannot address a {typeof(T).Name} stream by a {typeof(TId).Name}: " +
            $"it addresses streams by {(IsGuidIdentity ? "Guid" : "string")} or a strong-typed identity wrapping " +
            "one. Natural keys are not supported; switch to Marten, Polecat or Fisher when you need them.");
    }
}
