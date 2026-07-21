using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
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
    ///     jasperfx#420 — the configured default cap on how many projection rebuild cells may run
    ///     concurrently within a single database during a rebuild operation. <c>null</c> means
    ///     "unbounded" to JasperFx.Events, which is store-agnostic and has no notion of a connection
    ///     pool. Concrete stores SHOULD override this to consult the
    ///     <see cref="Daemon.DaemonSettings.MaxConcurrentRebuildsPerDatabase" /> knob and fall back to
    ///     a derived default (Marten/Polecat derive it from the Npgsql connection pool size —
    ///     <c>max(1, poolSize / 8)</c>). The <c>projections rebuild --max-concurrent</c> CLI flag
    ///     overrides this per operation; see
    ///     <see cref="CommandLine.ProjectionInput.ResolveMaxDegreeOfParallelism" />.
    /// </summary>
    int? MaxConcurrentRebuildsPerDatabase => null;

    /// <summary>
    ///     When true, a node-distributed async daemon (e.g. Wolverine-managed event-subscription
    ///     distribution) must fan out one subscription agent per (shard, tenant) rather than a single
    ///     store-global agent per (shard, database). Required for stores where multiple tenants are
    ///     co-located in one database and each tenant draws its own event sequence (sharded databases +
    ///     per-tenant event partitioning): a single store-global high-water cannot track tenants whose
    ///     independent sequences overlap, so a lagging tenant's later appends would fall below it and be
    ///     skipped. Default false (one agent per shard×database) — correct for store-global, single-database
    ///     per-tenant partitioning, and database-per-tenant stores. See jasperfx/wolverine#3280.
    /// </summary>
    bool DistributesAgentsPerTenant => false;

    /// <summary>
    ///     Whether extended progression tracking is enabled for this store — the store-level opt-in that
    ///     creates the extended progression columns (heartbeat, agent_status, pause_reason,
    ///     running_on_node) and, as of jasperfx#537, gates the daemon's write path onto them: when true,
    ///     the async daemon subscribes an <see cref="Daemon.ExtendedProgressionWriter" /> publication of
    ///     agent status transitions and heartbeat ticks through
    ///     <see cref="IEventDatabase.WriteExtendedProgressionAsync" />. Concrete stores override this to
    ///     reflect their own opt-in flag (Marten's <c>EnableExtendedProgressionTracking</c> /
    ///     <see cref="IEventStoreInstrumentation.ExtendedProgressionEnabled" />); the default is false so
    ///     nothing changes for stores that have not opted in. Read live per publication, so a runtime
    ///     flip is honored.
    /// </summary>
    bool ExtendedProgressionEnabled => false;

    /// <summary>
    ///     Resolve every <see cref="IEventDatabase" /> backing this event store, store-agnostically.
    ///     This is the store-neutral counterpart to Marten's <c>IMartenStorage.AllDatabases()</c> — it lets
    ///     monitoring/tooling code (e.g. CritterWatch) obtain an <see cref="IEventDatabase" /> to call the read
    ///     abstractions (<c>AllProjectionProgress</c>, <c>FetchDeadLetterCountsAsync</c>, <c>CountDeadLetterEventsAsync</c>)
    ///     without referencing concrete store types. The default implementation returns an empty array as a
    ///     stand-in; event stores should override this to return their real databases. See jasperfx#387.
    /// </summary>
    ValueTask<IReadOnlyList<IEventDatabase>> AllDatabases()
        => ValueTask.FromResult<IReadOnlyList<IEventDatabase>>([]);

    /// <summary>
    ///     Build a standalone, display-only high-water monitor for a single database — just the high-water agent,
    ///     with no projection shards attached, so a monitoring tool can show a live event-store "ceiling" for a
    ///     store whose projections are all Inline/Live and therefore run no async daemon. This is the abstraction
    ///     CritterWatch reaches for instead of <see cref="BuildProjectionDaemonAsync(string?, ILogger?)" /> when it
    ///     only wants the store's head sequence to progress, not any projection to run. The caller owns the
    ///     lifecycle: start the returned monitor on exactly one node (reuse the host's leader/agent election) and
    ///     stop it when the node stands down. The default implementation throws <see cref="NotSupportedException" />;
    ///     event stores (Marten, Polecat) override this to construct a <see cref="HighWaterMonitor" /> from their own
    ///     <see cref="IHighWaterDetector" /> and <see cref="DaemonSettings" />.
    ///     See <see href="https://github.com/JasperFx/CritterWatch/issues/675" />.
    /// </summary>
    /// <param name="tenantIdOrDatabaseIdentifier">
    ///     Resolves which database to monitor, matching <see cref="BuildProjectionDaemonAsync(string?, ILogger?)" />.
    ///     Null selects the default/main database.
    /// </param>
    /// <param name="logger">Optional logger for the monitor's diagnostic output.</param>
    ValueTask<IHighWaterMonitor> BuildHighWaterMonitorAsync(
        string? tenantIdOrDatabaseIdentifier = null,
        ILogger? logger = null)
        => throw new NotSupportedException(
            "BuildHighWaterMonitorAsync is not implemented on this IEventStore. Use an event store (Marten or Polecat) that supports standing up a standalone display-only high-water detector.");

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
    /// Return the most recently updated streams within a single tenant partition. A null
    /// <paramref name="tenantId"/> is store-global and delegates to the tenant-less overload
    /// (today's behavior). Event stores that implement multi-tenancy override this to open a
    /// tenant-scoped session; the default throws for a non-null tenant. See jasperfx#503.
    /// </summary>
    /// <param name="count">Maximum number of streams to return.</param>
    /// <param name="tenantId">Tenant partition to scope the listing to. Null means store-global.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<StreamSummary>> GetRecentStreamsAsync(int count, string? tenantId, CancellationToken ct)
        => tenantId == null
            ? GetRecentStreamsAsync(count, ct)
            : throw new NotSupportedException(
                "Per-tenant GetRecentStreamsAsync is not implemented on this IEventStore. Use an event store that implements multi-tenancy.");

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
    /// Stream the events of a single stream within a single tenant partition. A null
    /// <paramref name="tenantId"/> is store-global and delegates to the tenant-less overload
    /// (today's behavior). This overload exists because on a conjoined multi-tenant store the same
    /// stream id can exist under two tenants, so the tenant-less read resolves whichever tenant the
    /// default session happens to carry. Event stores that implement multi-tenancy override this to
    /// open a tenant-scoped session; the default throws for a non-null tenant rather than silently
    /// returning another tenant's events. See jasperfx#503.
    /// </summary>
    /// <param name="streamId">String form of the stream identifier.</param>
    /// <param name="tenantId">Tenant partition to read the stream from. Null means store-global.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<EventRecord> ReadStreamAsync(string streamId, string? tenantId, CancellationToken ct)
        => tenantId == null
            ? ReadStreamAsync(streamId, ct)
            : throw new NotSupportedException(
                "Per-tenant ReadStreamAsync is not implemented on this IEventStore. Use an event store that implements multi-tenancy.");

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
    /// Return full diagnostic metadata for a single stream within a single tenant partition. A null
    /// <paramref name="tenantId"/> is store-global and delegates to the tenant-less overload
    /// (today's behavior). Event stores that implement multi-tenancy override this to open a
    /// tenant-scoped session; the default throws for a non-null tenant. See jasperfx#503.
    /// </summary>
    /// <param name="streamId">String form of the stream identifier.</param>
    /// <param name="tenantId">Tenant partition to resolve the stream in. Null means store-global.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream metadata, or <see langword="null"/> when no stream exists with that id in that tenant.</returns>
    Task<StreamMetadata?> GetStreamMetadataAsync(string streamId, string? tenantId, CancellationToken ct)
        => tenantId == null
            ? GetStreamMetadataAsync(streamId, ct)
            : throw new NotSupportedException(
                "Per-tenant GetStreamMetadataAsync is not implemented on this IEventStore. Use an event store that implements multi-tenancy.");

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
    /// Return a snapshot of every projection's status scoped to a single tenant partition.
    /// A null <paramref name="tenantId"/> is store-global and delegates to the tenant-less
    /// overload (today's behavior). Event stores that implement per-tenant partitioning
    /// override this; the default throws for a non-null tenant. See jasperfx#407.
    /// </summary>
    /// <param name="tenantId">Tenant partition to scope statuses to. Null means store-global.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProjectionStatus>> GetProjectionStatusesAsync(string? tenantId, CancellationToken ct)
        => tenantId == null
            ? GetProjectionStatusesAsync(ct)
            : throw new NotSupportedException(
                "Per-tenant GetProjectionStatusesAsync is not implemented on this IEventStore. Use an event store that implements per-tenant partitioning.");

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

    /// <summary>
    /// Replay a projection referenced by name over a fixed in-memory event list,
    /// fanning the events out across every aggregate identity the projection touches
    /// and returning one <see cref="ProjectionTimelineRaw"/> per identity. Unlike
    /// <see cref="RunProjectionByNameAsync"/> this drives the projection's full
    /// slice → group → enrich → fold path against a live query session, so multi-stream
    /// projections produce a timeline per resulting identity and single-stream
    /// projections produce exactly one. Stateless — nothing is persisted. The default
    /// implementation throws <see cref="NotImplementedException"/>.
    /// </summary>
    /// <remarks>
    /// Enrichment reads present-day reference data even when replaying historical events;
    /// enriched values reflect the current state of that reference data, not its state at
    /// the time the events were originally recorded.
    /// </remarks>
    /// <param name="projectionName">Name of the projection to replay.</param>
    /// <param name="events">Events to feed into the projection in global apply order.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MultiAggregateProjectionResult> RunMultiStreamProjectionAsync(
        string projectionName,
        IReadOnlyList<EventRecord> events,
        CancellationToken ct)
        => throw new NotImplementedException(
            "RunMultiStreamProjectionAsync is not implemented on this IEventStore. Use Marten or Polecat 6+ for projection step-through.");
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

    /// <summary>
    /// Delete *only* any persisted projection progress data for a single tenant partition.
    /// A null <paramref name="tenantId"/> is store-global and delegates to the tenant-less
    /// overload (today's behavior). Event stores that implement per-tenant partitioning
    /// override this; the default throws for a non-null tenant. See jasperfx#407.
    /// </summary>
    Task DeleteProjectionProgressAsync(IEventDatabase database, string subscriptionName, string? tenantId,
        CancellationToken token)
        => tenantId == null
            ? DeleteProjectionProgressAsync(database, subscriptionName, token)
            : throw new NotSupportedException(
                "Per-tenant DeleteProjectionProgressAsync is not implemented on this IEventStore. Use an event store that implements per-tenant partitioning.");

    ValueTask<IProjectionBatch<TOperations, TQuerySession>> StartProjectionBatchAsync(EventRange range,
        IEventDatabase database, ShardExecutionMode mode, AsyncOptions projectionOptions, CancellationToken token);
    
    IEventLoader BuildEventLoader(IEventDatabase database, ILogger loggerFactory, EventFilterable filtering,
        AsyncOptions shardOptions);

    /// <summary>
    /// Build an event loader for a specific shard. Stores that opt into per-tenant partitioning should
    /// consume <paramref name="shardName" />.TenantId to scope event loading to a single tenant's
    /// partition when non-null. Default behavior stays partition-agnostic and ignores it, delegating to
    /// the shard-less overload — so out-of-tree stores keep compiling. jasperfx#407 Phase 2c.
    /// </summary>
    IEventLoader BuildEventLoader(IEventDatabase database, ILogger loggerFactory, EventFilterable filtering,
        AsyncOptions shardOptions, ShardName shardName)
        => BuildEventLoader(database, loggerFactory, filtering, shardOptions);

    TOperations OpenSession(IEventDatabase database);
    TOperations OpenSession(IEventDatabase database, string tenantId);
    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);
}