#nullable enable

using JasperFx.Core;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

public interface IProjectionCleanupActions;

public record DeleteDocuments(Type DocumentType): IProjectionCleanupActions;

public record DeleteTableData(string TableIdentifier): IProjectionCleanupActions;

/// <summary>
///     Governs the advanced behavior of a projection shard running
///     in the projection daemon
/// </summary>
public class AsyncOptions
{
    private readonly List<IProjectionCleanupActions> _cleanups = new();

    public IReadOnlyList<IProjectionCleanupActions> CleanUps => _cleanups;

    /// <summary>
    ///     The maximum range of events fetched at one time
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    ///     The maximum number of events to be held in memory in preparation
    ///     for determining projection updates.
    /// </summary>
    public int MaximumHopperSize { get; set; } = 5000;

    /// <summary>
    ///     Optional list of stored document or feature types that this projection
    ///     writes. This is used by Marten to help build out schema objects if the
    ///     async daemon is started before the rest of the application.
    /// </summary>
    public List<Type> StorageTypes { get; } = new();

    /// <summary>
    /// Enable the identity map mechanics to reuse documents within the session by their identity
    /// if a projection needs to make subsequent changes to the same document at one time. Default is no tracking
    /// </summary>
    public bool EnableDocumentTrackingByIdentity { get; set; }

    public bool TeardownDataOnRebuild { get; set; } = true;

    /// <summary>
    /// If more than 0 (the default), this is the maximum number of aggregates
    /// that will be cached in a 2nd level, most recently used cache during async
    /// projection. Use this to potentially improve async projection throughput
    /// </summary>
    public int CacheLimitPerTenant { get; set; } = 0;

    /// <summary>
    ///     Add explicit teardown rule to delete all documents of type T
    ///     when this projection shard is rebuilt
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void DeleteViewTypeOnTeardown<T>()
    {
        DeleteViewTypeOnTeardown(typeof(T));
    }

    /// <summary>
    ///     Add explicit teardown rule to delete all documents of type T
    ///     when this projection shard is rebuilt
    /// </summary>
    /// <param name="type"></param>
    public void DeleteViewTypeOnTeardown(Type type)
    {
        _cleanups.Add(new DeleteDocuments(type));
        StorageTypes.Add(type);
    }

    /// <summary>
    ///     Add an explicit teardown rule to wipe data in the named table
    ///     when this projection shard is rebuilt
    /// </summary>
    /// <param name="name"></param>
    public void DeleteDataInTableOnTeardown(string tableIdentifier)
    {
        _cleanups.Add(new DeleteTableData(tableIdentifier));
    }

    internal Task<Position> DetermineStartingPositionAsync(long highWaterMark, ShardName name, ShardExecutionMode mode,
        IEventDatabase database,
        CancellationToken token)
    {
        var strategy = matchStrategy(database);
        return strategy.DetermineStartingPositionAsync(highWaterMark, name, mode, database, token);
    }

    private IPositionStrategy matchStrategy(IEventDatabase database)
    {
        return _strategies.Where(x => x.DatabaseIdentifier.IsNotEmpty()).FirstOrDefault(x => x.DatabaseIdentifier!.EqualsIgnoreCase(database.Identifier))
                       ?? _strategies.FirstOrDefault(x => x.DatabaseIdentifier.IsEmpty()) ?? CatchUp.Instance;
    }

    private readonly List<IPositionStrategy> _strategies = new();

    /// <summary>
    /// Direct that this subscription or projection should only start from events that are appended
    /// after the subscription is started
    /// </summary>
    /// <param name="databaseIdentifier">Optionally applies this rule to *only* the named database in the case of
    /// using a multi-tenancy per multiple databases strategy</param>
    /// <returns></returns>
    public AsyncOptions SubscribeFromPresent(string? databaseIdentifier = null)
    {
        _strategies.Add(new FromPresent(databaseIdentifier));
        return this;
    }

    /// <summary>
    /// Direct that this subscription or projection should only start from events that have a timestamp
    /// greater than the supplied eventTimestampFloor
    /// </summary>
    /// <param name="eventTimestampFloor">The floor time of the events where this subscription should be started</param>
    /// <param name="databaseIdentifier">Optionally applies this rule to *only* the named database in the case of
    /// using a multi-tenancy per multiple databases strategy</param>
    /// <returns></returns>
    public AsyncOptions SubscribeFromTime(DateTimeOffset eventTimestampFloor, string? databaseIdentifier = null)
    {
        _strategies.Add(new FromTime(databaseIdentifier, eventTimestampFloor));
        return this;
    }

    /// <summary>
    /// Direct that this subscription or projection should only start from events that have a sequence
    /// greater than the supplied sequenceFloor
    /// </summary>
    /// <param name="sequenceFloor"></param>
    /// <param name="databaseIdentifier">Optionally applies this rule to *only* the named database in the case of
    /// using a multi-tenancy per multiple databases strategy</param>
    /// <returns></returns>
    public AsyncOptions SubscribeFromSequence(long sequenceFloor, string? databaseIdentifier = null)
    {
        _strategies.Add(new FromSequence(databaseIdentifier, sequenceFloor));
        return this;
    }

    /// <summary>
    /// Use this option to prevent having to rebuild a projection when you are
    /// simply changing the projection lifecycle from "Inline" to "Async" but are
    /// making no other changes that would force a rebuild
    ///
    /// Direct that this projection had previously been running with an "Inline"
    /// lifecycle now run as "Async". This will cause Marten to first check if there
    /// is any previous async progress, and if not, start the projection from the highest
    /// event sequence for the system.
    /// </summary>
    /// <returns></returns>
    public AsyncOptions SubscribeAsInlineToAsync()
    {
        _strategies.Add(new InlineToAsync());
        return this;
    }
}

internal record Position(long Floor, bool ShouldUpdateProgressFirst);

internal interface IPositionStrategy
{
    string? DatabaseIdentifier { get;}

    Task<Position> DetermineStartingPositionAsync(long highWaterMark, ShardName name, ShardExecutionMode mode,
        IEventDatabase database,
        CancellationToken token);
}

internal class InlineToAsync(): IPositionStrategy
{
    public string? DatabaseIdentifier => null;

    public async Task<Position> DetermineStartingPositionAsync(long highWaterMark, ShardName name, ShardExecutionMode mode,
        IEventDatabase database, CancellationToken token)
    {
        var current = await database.ProjectionProgressFor(name, token).ConfigureAwait(false);
        if (current > 0)
        {
            return new Position(current, false);
        }

        var highest = await database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
        return new Position(highest, true);
    }
}

internal class FromSequence(string? databaseName, long sequence): IPositionStrategy
{
    public string? DatabaseIdentifier { get; } = databaseName;
    public long Sequence { get; } = sequence;

    public async Task<Position> DetermineStartingPositionAsync(long highWaterMark, ShardName name,
        ShardExecutionMode mode,
        IEventDatabase database, CancellationToken token)
    {
        if (mode == ShardExecutionMode.Rebuild)
        {
            return new Position(Sequence, true);
        }

        var current = await database.ProjectionProgressFor(name, token).ConfigureAwait(false);

        return current >= Sequence
            ? new Position(current, false)
            : new Position(Sequence, true);
    }
}

internal class FromTime(string? databaseName, DateTimeOffset time): IPositionStrategy
{
    public string? DatabaseIdentifier { get; } = databaseName;
    public DateTimeOffset EventFloorTime { get; } = time;

    public async Task<Position> DetermineStartingPositionAsync(long highWaterMark, ShardName name,
        ShardExecutionMode mode,
        IEventDatabase database, CancellationToken token)
    {
        var floor = await database.FindEventStoreFloorAtTimeAsync(EventFloorTime, token).ConfigureAwait(false) ?? 0;

        if (mode == ShardExecutionMode.Rebuild)
        {
            return new Position(floor, true);
        }

        var current = await database.ProjectionProgressFor(name, token).ConfigureAwait(false);

        return current >= floor ? new Position(current, false) : new Position(floor, true);
    }
}

internal class FromPresent(string? databaseName): IPositionStrategy
{
    public string? DatabaseIdentifier { get; } = databaseName;

    public Task<Position> DetermineStartingPositionAsync(long highWaterMark, ShardName name, ShardExecutionMode mode,
        IEventDatabase database, CancellationToken token)
    {
        return Task.FromResult(new Position(highWaterMark, true));
    }
}

internal class CatchUp: IPositionStrategy
{
    internal static CatchUp Instance = new();

    private CatchUp(){}

    public string? DatabaseIdentifier { get; set; } = null;

    public async Task<Position> DetermineStartingPositionAsync(long highWaterMark, ShardName name,
        ShardExecutionMode mode,
        IEventDatabase database,
        CancellationToken token)
    {
        return mode == ShardExecutionMode.Continuous
            ? new Position(await database.ProjectionProgressFor(name, token).ConfigureAwait(false), false)

            // No point in doing the extra database hop
            : new Position(0, true);
    }
}

