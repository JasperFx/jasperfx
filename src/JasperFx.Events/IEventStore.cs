using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events;

public interface IEventStore
{
    Task<EventStoreUsage?> TryCreateUsage(CancellationToken token);
    Uri Subject { get; }

    ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
        string? tenantIdOrDatabaseIdentifier = null,
        ILogger? logger = null);
}

public interface IEventStore<TOperations, TQuerySession> : IEventStore where TOperations : TQuerySession, IStorageOperations
{
    IEventRegistry Registry { get; }

    Type IdentityTypeForProjectedType(Type aggregateType);
    
    string DefaultDatabaseName { get; }
    ErrorHandlingOptions ContinuousErrors { get; }
    ErrorHandlingOptions RebuildErrors { get; }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> AllShards();
    
    Meter Meter { get; }
    
    ActivitySource ActivitySource { get; }
    
    /// <summary>
    /// TimeProvider used for event timestamping metadata. Replace for controlling the timestamps
    /// in testing
    /// </summary>
    public TimeProvider TimeProvider { get; }

    string MetricsPrefix { get;}
    AutoCreate AutoCreateSchemaObjects { get; }

    Task RewindSubscriptionProgressAsync(IEventDatabase database, string subscriptionName, CancellationToken token, long? sequenceFloor);

    Task RewindAgentProgressAsync(IEventDatabase database, string shardName, CancellationToken token, long sequenceFloor);

    Task TeardownExistingProjectionProgressAsync(IEventDatabase database, string subscriptionName,
        CancellationToken token);

    ValueTask<IProjectionBatch<TOperations, TQuerySession>> StartProjectionBatchAsync(EventRange range,
        IEventDatabase database, ShardExecutionMode mode, AsyncOptions projectionOptions, CancellationToken token);
    
    IEventLoader BuildEventLoader(IEventDatabase database, ILogger loggerFactory, EventFilterable filtering,
        AsyncOptions shardOptions);

    TOperations OpenSession(IEventDatabase database);
    TOperations OpenSession(IEventDatabase database, string tenantId);
    ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode);
}