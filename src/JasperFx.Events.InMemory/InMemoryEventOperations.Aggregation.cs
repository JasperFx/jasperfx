namespace JasperFx.Events.InMemory;

public partial class InMemoryEventOperations
{
    // jasperfx#964 phase 2 (live aggregation) replaces these. Until then they say so plainly.
    private static NotSupportedException notYet(string member)
        => new($"{member} is not available on the in-memory prototyping store yet (jasperfx#964, live aggregation).");

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(Guid id, CancellationToken cancellation = default) where T : class
        => throw notYet(nameof(FetchForWriting));

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(string key, CancellationToken cancellation = default) where T : class
        => throw notYet(nameof(FetchForWriting));

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(Guid id, long expectedVersion, CancellationToken cancellation = default)
        where T : class
        => throw notYet(nameof(FetchForWriting));

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForWriting<T>(string key, long expectedVersion, CancellationToken cancellation = default)
        where T : class
        => throw notYet(nameof(FetchForWriting));

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
        => throw notYet(nameof(FetchForWriting));

    /// <inheritdoc />
    public Task<IEventStream<T>> FetchForExclusiveWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => FetchForWriting<T, TId>(id, cancellation);

    /// <inheritdoc />
    public Task WriteToAggregate<T>(Guid id, Action<IEventStream<T>> writing, CancellationToken cancellation = default)
        where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing, CancellationToken cancellation = default)
        where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteToAggregate<T>(string id, Action<IEventStream<T>> writing, CancellationToken cancellation = default)
        where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteToAggregate<T>(string id, Func<IEventStream<T>, Task> writing, CancellationToken cancellation = default)
        where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteToAggregate<T>(Guid id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteToAggregate<T>(Guid id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteToAggregate<T>(string id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteToAggregate<T>(string id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteToAggregate));

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteExclusivelyToAggregate));

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteExclusivelyToAggregate));

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteExclusivelyToAggregate));

    /// <inheritdoc />
    public Task WriteExclusivelyToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class => throw notYet(nameof(WriteExclusivelyToAggregate));

    /// <inheritdoc />
    public ValueTask<T?> FetchLatest<T>(Guid id, CancellationToken cancellation = default) where T : class
        => throw notYet(nameof(FetchLatest));

    /// <inheritdoc />
    public ValueTask<T?> FetchLatest<T>(string id, CancellationToken cancellation = default) where T : class
        => throw notYet(nameof(FetchLatest));

    /// <inheritdoc />
    public ValueTask<T?> FetchLatest<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => throw notYet(nameof(FetchLatest));

    /// <inheritdoc />
    public ValueTask<T?> ProjectLatest<T>(Guid id, CancellationToken cancellation = default) where T : class
        => throw notYet(nameof(ProjectLatest));

    /// <inheritdoc />
    public ValueTask<T?> ProjectLatest<T>(string id, CancellationToken cancellation = default) where T : class
        => throw notYet(nameof(ProjectLatest));

    /// <inheritdoc />
    public Task<T?> AggregateStreamAsync<T>(Guid streamId, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class
        => throw notYet(nameof(AggregateStreamAsync));

    /// <inheritdoc />
    public Task<T?> AggregateStreamAsync<T>(string streamKey, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class
        => throw notYet(nameof(AggregateStreamAsync));

    /// <inheritdoc />
    public Task<T?> AggregateStreamToLastKnownAsync<T>(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, CancellationToken token = default) where T : class
        => throw notYet(nameof(AggregateStreamToLastKnownAsync));

    /// <inheritdoc />
    public Task<T?> AggregateStreamToLastKnownAsync<T>(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, CancellationToken token = default) where T : class
        => throw notYet(nameof(AggregateStreamToLastKnownAsync));
}
