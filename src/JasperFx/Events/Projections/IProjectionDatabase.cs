#nullable enable
namespace JasperFx.Events.Projections;

// NOTES -- this will be wrapped around MartenDatabase & DocumentStore

public interface IProjectionStorage
{
    /// <summary>
    ///     Check the current progress of a single projection or projection shard
    /// </summary>
    /// <param name="tenantId">
    ///     Specify the database containing this tenant id. If omitted, this method uses the default
    ///     database
    /// </param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<long> ProjectionProgressFor(ShardName name,
        CancellationToken token = default);

    /// <summary>
    /// Find the position of the event store sequence just below the supplied timestamp. Will
    /// return null if there are no events below that time threshold
    /// </summary>
    /// <param name="timestamp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token);
    
    string StorageIdentifier { get; }
}

// NOTES -- maybe just have this implemented by ProjectionDocumentSession as is

public interface IProjectionStorageSession
{
    void DeleteForType(Type documentType);
    void DeleteNamedResource(string resourceName);
}
