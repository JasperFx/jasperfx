using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.NewStuff;

public interface IEventStorage<TOperations, TQuerySession> where TOperations : TQuerySession
{
    IEventRegistry Registry { get; }
    
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
        IEventDatabase database, ShardExecutionMode mode, CancellationToken token);
    
    // TODO -- add tenants here later?
    IEventLoader BuildEventLoader(IEventDatabase database, ILoggerFactory loggerFactory, EventFilterable filtering);
}