using JasperFx.Events.Descriptors;

namespace JasperFx.Events.CommandLine;

public enum RebuildStatus
{
    NoData,
    Complete,
}

public record EventStoreDatabase(Uri SubjectUri, string DatabaseIdentifier);

public interface IProjectionHost
{
    // TODO -- this will have to be async
    Task<IReadOnlyList<EventStoreUsage>> AllStoresAsync();
    void ListenForUserTriggeredExit();
    Task<RebuildStatus> TryRebuildShardsAsync(EventStoreDatabase databaseIdentifier,
        string[] names, TimeSpan? shardTimeout = null);
    Task StartShardsAsync(EventStoreDatabase databaseIdentifier, string[] projectionNames);
    Task WaitForExit();
}
