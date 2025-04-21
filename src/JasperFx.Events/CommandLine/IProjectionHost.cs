using JasperFx.Events.Descriptors;

namespace JasperFx.Events.CommandLine;

public enum RebuildStatus
{
    NoData,
    Complete,
}

public record EventStoreDatabaseIdentifier(Uri SubjectUri, string DatabaseIdentifier);

public interface IProjectionHost
{
    Task<IReadOnlyList<EventStoreUsage>> AllStoresAsync();
    void ListenForUserTriggeredExit();
    Task<RebuildStatus> TryRebuildShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier,
        string[] names, TimeSpan? shardTimeout = null);
    Task StartShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier, string[] projectionNames);
    Task WaitForExitAsync();
    Task AdvanceHighWaterMarkToLatestAsync(ProjectionSelection selection, CancellationToken none);
}
