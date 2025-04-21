using JasperFx.Events.Descriptors;

namespace JasperFx.Events.CommandLine;

public interface IConsoleView
{
    void DisplayNoStoresMessage();
    void ListShards(IReadOnlyList<EventStoreUsage> usages);
    void DisplayEmptyEventsMessage(EventStoreDatabaseIdentifier usage);

    void DisplayNoAsyncProjections();
    void DisplayRebuildIsComplete();
    void DisplayInvalidShardTimeoutValue();
    void WriteStartingToRebuildProjections(ProjectionSelection selection, string databaseName);
}
