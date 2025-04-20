using JasperFx.Events.Descriptors;

namespace JasperFx.Events.CommandLine;

public interface IConsoleView
{
    void DisplayNoStoresMessage();
    void ListShards(EventStoreUsage[] usages);
    void DisplayEmptyEventsMessage(EventStoreUsage usage);
    string[] SelectStores(string[] storeNames);
    string[] SelectProjections(string[] projectionNames);
    void DisplayNoMatchingProjections();
    void WriteHeader(EventStoreUsage usage);
    void DisplayNoDatabases();
    void DisplayNoAsyncProjections();
    void WriteHeader(IEventDatabase database);
    string[] SelectDatabases(string[] databaseNames);
    void DisplayRebuildIsComplete();
    void DisplayInvalidShardTimeoutValue();
}
