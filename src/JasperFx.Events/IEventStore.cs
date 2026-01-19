using System.Diagnostics;
using System.Diagnostics.Metrics;
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