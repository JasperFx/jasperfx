using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.NewStuff;

public interface IEventDatabase : IProjectionStorage
{
    /// <summary>
    ///     Identifying name for infrastructure and logging
    /// </summary>
    string Identifier { get; }
    
    /// <summary>
    ///     *If* a projection daemon has been started for this database, this
    ///     is the ShardStateTracker for the running daemon. This is useful in testing
    ///     scenarios
    /// </summary>
    ShardStateTracker Tracker { get; }

    Task StoreDeadLetterEventAsync(DeadLetterEvent deadLetterEvent, CancellationToken token);

    Task EnsureStorageExistsAsync(Type storageType, CancellationToken token);

    Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout);
}