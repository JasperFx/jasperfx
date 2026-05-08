using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events;

public record EventStoreIdentity(string Name, string Type)
{
    public override string ToString()
    {
        return $"{Name}:{Type}";
    }
}

public interface IEventStore
{
    Task<EventStoreUsage?> TryCreateUsage(CancellationToken token);
    Uri Subject { get; }

    ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
        string? tenantIdOrDatabaseIdentifier = null,
        ILogger? logger = null);
    
    ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id);

    Meter Meter { get; }
    
    ActivitySource ActivitySource { get; }

    string MetricsPrefix { get; }
    
    DatabaseCardinality DatabaseCardinality { get; }
    bool HasMultipleTenants { get; }
    
    /// <summary>
    /// Identifies the event store within an application
    /// </summary>
    EventStoreIdentity Identity { get; }

    /// <summary>
    /// Open a read-only event store session for querying
    /// </summary>
    IReadOnlyEventStore OpenReadOnlyEventStore();

    /// <summary>
    /// Compact a stream by aggregating events into a snapshot.
    /// Resolves aggregate type from stream state.
    /// </summary>
    Task CompactStreamAsync(Guid streamId, CancellationToken token = default);

    /// <summary>
    /// Compact a stream by aggregating events into a snapshot.
    /// Resolves aggregate type from stream state.
    /// </summary>
    Task CompactStreamAsync(string streamKey, CancellationToken token = default);

    /// <summary>
    /// Return a lightweight summary of the most recently updated streams,
    /// ordered newest first. Powers the event store explorer's stream list
    /// view. The default implementation throws <see cref="NotImplementedException"/>;
    /// concrete event stores (Marten, Polecat) override it.
    /// </summary>
    /// <param name="count">Maximum number of streams to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<StreamSummary>> GetRecentStreamsAsync(int count, CancellationToken ct)
        => throw new NotImplementedException(
            "GetRecentStreamsAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for the event store explorer.");

    /// <summary>
    /// Stream the events of a single stream from oldest to newest. Powers
    /// the explorer's stream-detail timeline. The default implementation
    /// throws <see cref="NotImplementedException"/>.
    /// </summary>
    /// <param name="streamId">String form of the stream identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<EventRecord> ReadStreamAsync(string streamId, CancellationToken ct)
        => throw new NotImplementedException(
            "ReadStreamAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for the event store explorer.");

    /// <summary>
    /// Return full diagnostic metadata for a single stream — version,
    /// timestamps, snapshot info, archive flag, tags. The default
    /// implementation throws <see cref="NotImplementedException"/>.
    /// </summary>
    /// <param name="streamId">String form of the stream identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream metadata, or <see langword="null"/> when no stream exists with that id.</returns>
    Task<StreamMetadata?> GetStreamMetadataAsync(string streamId, CancellationToken ct)
        => throw new NotImplementedException(
            "GetStreamMetadataAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for the event store explorer.");

    /// <summary>
    /// Stream events that match all of the supplied DCB tag values.
    /// Powers the explorer's tag-query view. The default implementation
    /// throws <see cref="NotImplementedException"/>.
    /// </summary>
    /// <param name="tags">Tag-name -> tag-value pairs to match.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<EventRecord> QueryByTagsAsync(
        IReadOnlyDictionary<string, string> tags, CancellationToken ct)
        => throw new NotImplementedException(
            "QueryByTagsAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for DCB tag queries.");

    /// <summary>
    /// Rehydrate a DCB-projected entity by tag set, returning the
    /// projected state at its current version. The default implementation
    /// throws <see cref="NotImplementedException"/>.
    /// </summary>
    /// <param name="projectionName">Configured name of the projection.</param>
    /// <param name="tags">Tag-name -> tag-value pairs that scope the projection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Projected state, or <see langword="null"/> when no events match the tag set.</returns>
    Task<DcbProjectedState?> GetProjectedStateForTagsAsync(
        string projectionName,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct)
        => throw new NotImplementedException(
            "GetProjectedStateForTagsAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for DCB tag queries.");

    /// <summary>
    /// Rehydrate an aggregate to a specific stream version using a
    /// strong-typed aggregate. The default implementation throws
    /// <see cref="NotImplementedException"/>.
    /// </summary>
    /// <typeparam name="TAggregate">CLR aggregate type.</typeparam>
    /// <param name="identity">Aggregate identity.</param>
    /// <param name="version">Stream version to rehydrate at.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AggregateAtVersion<TAggregate>> RehydrateAtVersionAsync<TAggregate>(
        object identity, long version, CancellationToken ct) where TAggregate : class
        => throw new NotImplementedException(
            "RehydrateAtVersionAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for aggregate rewind.");

    /// <summary>
    /// Rehydrate an aggregate referenced by name to a specific stream
    /// version, returning the projected state as JSON. The default
    /// implementation throws <see cref="NotImplementedException"/>.
    /// </summary>
    /// <param name="aggregateTypeName">FullName of the aggregate's CLR type.</param>
    /// <param name="identity">Aggregate identity.</param>
    /// <param name="version">Stream version to rehydrate at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate state at the requested version, or <see langword="null"/> when no aggregate exists for that identity.</returns>
    Task<AggregateAtVersion?> RehydrateAtVersionByNameAsync(
        string aggregateTypeName, object identity, long version, CancellationToken ct)
        => throw new NotImplementedException(
            "RehydrateAtVersionByNameAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for aggregate rewind.");

    /// <summary>
    /// Return a snapshot of every projection's status. Live updates flow
    /// over the existing <c>ShardStatesChanged</c> event so monitoring
    /// tools can render an initial table from this snapshot and then
    /// subscribe for changes. The default implementation throws
    /// <see cref="NotImplementedException"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProjectionStatus>> GetProjectionStatusesAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "GetProjectionStatusesAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for the projections page.");

    /// <summary>
    /// Replay a projection over a fixed in-memory event list, returning
    /// the per-step before/after state. Stateless — nothing is persisted
    /// and no server-side session is created. Powers the explorer's
    /// projection-stepper view. The default implementation throws
    /// <see cref="NotImplementedException"/>.
    /// </summary>
    /// <typeparam name="TState">CLR type of the projection's state.</typeparam>
    /// <param name="projectionName">Name of the projection to replay.</param>
    /// <param name="identity">Aggregate / scope identity used by the projection.</param>
    /// <param name="events">Events to feed into the projection in apply order.</param>
    /// <param name="startingState">Optional initial state; <see langword="null"/> starts from the projection's default.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProjectionTimeline<TState>> RunProjectionAsync<TState>(
        string projectionName,
        object identity,
        IReadOnlyList<EventRecord> events,
        TState? startingState,
        CancellationToken ct)
        => throw new NotImplementedException(
            "RunProjectionAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for projection step-through.");

    /// <summary>
    /// Replay a projection referenced by name over a fixed in-memory
    /// event list, returning the per-step before/after state as JSON.
    /// Stateless — nothing is persisted. The default implementation throws
    /// <see cref="NotImplementedException"/>.
    /// </summary>
    /// <param name="projectionName">Name of the projection to replay.</param>
    /// <param name="identity">Aggregate / scope identity used by the projection.</param>
    /// <param name="events">Events to feed into the projection in apply order.</param>
    /// <param name="startingState">Optional initial state expressed as JSON; <see langword="null"/> starts from the projection's default.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProjectionTimelineRaw> RunProjectionByNameAsync(
        string projectionName,
        object identity,
        IReadOnlyList<EventRecord> events,
        JsonElement? startingState,
        CancellationToken ct)
        => throw new NotImplementedException(
            "RunProjectionByNameAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for projection step-through.");
}

public interface IEventStore<TOperations, TQuerySession> : IEventStore where TOperations : TQuerySession, IStorageOperations
{
    IEventRegistry Registry { get; }

    Type IdentityTypeForProjectedType(Type aggregateType);
    
    string DefaultDatabaseName { get; }
    ErrorHandlingOptions ContinuousErrors { get; }
    ErrorHandlingOptions RebuildErrors { get; }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> AllShards();
    
    /// <summary>
    /// TimeProvider used for event timestamping metadata. Replace for controlling the timestamps
    /// in testing
    /// </summary>
    public TimeProvider TimeProvider { get; }
    
    AutoCreate AutoCreateSchemaObjects { get; }

    Task RewindSubscriptionProgressAsync(IEventDatabase database, string subscriptionName, CancellationToken token, long? sequenceFloor);

    Task RewindAgentProgressAsync(IEventDatabase database, string shardName, CancellationToken token, long sequenceFloor);

    [Obsolete("This was badly named as in the current implementation it is really TeardownExistingProjectionStateAsync()")]
    Task TeardownExistingProjectionProgressAsync(IEventDatabase database, string subscriptionName,
        CancellationToken token);

    /// <summary>
    /// Tear down all stored projection data and the projection progression
    /// </summary>
    /// <param name="database"></param>
    /// <param name="subscriptionName"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task TeardownExistingProjectionStateAsync(IEventDatabase database, string subscriptionName,
        CancellationToken token);
    
    /// <summary>
    /// Delete *only* any persisted projection progress data
    /// </summary>
    /// <param name="database"></param>
    /// <param name="subscriptionName"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task DeleteProjectionProgressAsync(IEventDatabase database, string subscriptionName,
        CancellationToken token);

    ValueTask<IProjectionBatch<TOperations, TQuerySession>> StartProjectionBatchAsync(EventRange range,
        IEventDatabase database, ShardExecutionMode mode, AsyncOptions projectionOptions, CancellationToken token);
    
    IEventLoader BuildEventLoader(IEventDatabase database, ILogger loggerFactory, EventFilterable filtering,
        AsyncOptions shardOptions);

    TOperations OpenSession(IEventDatabase database);
    TOperations OpenSession(IEventDatabase database, string tenantId);
    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);
}