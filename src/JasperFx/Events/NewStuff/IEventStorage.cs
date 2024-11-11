using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.NewStuff;

public interface IEventStorage<TDatabase> where TDatabase : IEventDatabase
{
    string DefaultDatabaseName { get; }
    ErrorHandlingOptions ContinuousErrors { get; }
    ErrorHandlingOptions RebuildErrors { get; }

    IReadOnlyList<IAsyncShard<TDatabase>> AllShards();
    
    Meter Meter { get; }
    
    ActivitySource ActivitySource { get; }
    
    /// <summary>
    /// TimeProvider used for event timestamping metadata. Replace for controlling the timestamps
    /// in testing
    /// </summary>
    public TimeProvider TimeProvider { get; }

    string MetricsPrefix { get;}
    AutoCreate AutoCreateSchemaObjects { get; }

    Task RewindSubscriptionProgressAsync(TDatabase database, string subscriptionName, CancellationToken token, long? sequenceFloor);

    Task RewindAgentProgressAsync(TDatabase database, string shardName, CancellationToken token, long sequenceFloor);
    Task TeardownExistingProjectionProgressAsync<TDatabase>(TDatabase database, string subscriptionName, CancellationToken token) where TDatabase : IEventDatabase;
}