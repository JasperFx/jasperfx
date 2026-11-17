using System.Diagnostics;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     How a <see cref="ProjectionPositionWait" /> ended.
/// </summary>
public enum ProjectionWaitStatus
{
    /// <summary>
    ///     Every shard waited on has processed the position.
    /// </summary>
    Reached,

    /// <summary>
    ///     The timeout ran out first. The write succeeded; the projection is behind.
    /// </summary>
    TimedOut,

    /// <summary>
    ///     A shard that had not reached the position is paused, so waiting longer would not help.
    /// </summary>
    Paused,

    /// <summary>
    ///     A shard that had not reached the position is being rebuilt, so waiting longer would not help.
    /// </summary>
    Rebuilding
}

/// <summary>
///     How far one projection shard got, as of the last read of the progression table.
/// </summary>
/// <param name="Shard">The progression cell that was read, tenant-qualified when the store partitions by tenant.</param>
/// <param name="Sequence">The sequence the shard had processed through, or null when it has no progression row yet.</param>
/// <param name="Reached">True when the shard has processed the position.</param>
/// <param name="Paused">True when the shard's agent reported itself paused.</param>
/// <param name="Rebuilding">True when the shard is being rebuilt.</param>
public sealed record ProjectionShardWait(ShardName Shard, long? Sequence, bool Reached, bool Paused, bool Rebuilding)
{
    public override string ToString()
    {
        var state = Paused ? ", paused" : Rebuilding ? ", rebuilding" : string.Empty;
        return Sequence.HasValue
            ? $"{Shard.Identity} at {Sequence.Value}{state}"
            : $"{Shard.Identity} with no progress recorded{state}";
    }
}

/// <summary>
///     The answer to <see cref="IEventStore.WaitForProjectionsAsync(EventStorePosition, IReadOnlyList{Type}, TimeSpan, CancellationToken)" />.
///     A value rather than an exception, because a projection that is behind is not a failure: the caller
///     decides whether to read anyway, return a 202, or fall back to a live aggregation.
/// </summary>
/// <param name="Status">How the wait ended.</param>
/// <param name="Position">The position that was waited on.</param>
/// <param name="Shards">How far each shard got, as of the last read.</param>
/// <param name="Elapsed">How long the wait took.</param>
public sealed record ProjectionWaitResult(
    ProjectionWaitStatus Status,
    EventStorePosition Position,
    IReadOnlyList<ProjectionShardWait> Shards,
    TimeSpan Elapsed)
{
    /// <summary>
    ///     Every shard waited on has processed the position.
    /// </summary>
    public bool Reached => Status == ProjectionWaitStatus.Reached;

    /// <summary>
    ///     The timeout ran out before every shard processed the position. <see cref="Shards" /> says how far
    ///     each one got.
    /// </summary>
    public bool TimedOut => Status == ProjectionWaitStatus.TimedOut;

    public override string ToString()
    {
        return $"{Status} waiting for {Position} after {Elapsed}: [{string.Join("; ", Shards)}]";
    }
}

/// <summary>
///     The wait behind <see cref="IEventStore.WaitForProjectionsAsync(EventStorePosition, IReadOnlyList{Type}, TimeSpan, CancellationToken)" />:
///     poll one database's progression table until the named shards have processed a position the caller
///     holds (marten#6004).
/// </summary>
/// <remarks>
///     <para>
///         This differs from <see cref="NonStaleProjectionWait" /> in what it waits for. That one waits for the
///         highest sequence in the store as of the call, which on a busy store includes other writers' events.
///         This one waits for the caller's own position and nothing past it.
///     </para>
///     <para>
///         It reads the progression table rather than an in-process tracker, so it works on any node,
///         whether or not that node runs the shards.
///     </para>
///     <para>
///         Things it cannot do anything about: a sequence gap below the position holds the wait until the
///         daemon's gap detection skips it, and the daemon's own polling interval is the floor on how soon a
///         new event is projected.
///     </para>
/// </remarks>
public static class ProjectionPositionWait
{
    /// <summary>
    ///     How often the progression table is read while waiting.
    /// </summary>
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     Wait until every shard in <paramref name="shards" /> has processed <paramref name="position" /> on
    ///     <paramref name="database" />.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>Never throws on timeout. The result says <see cref="ProjectionWaitStatus.TimedOut" /> and how far
    ///         each shard got.</item>
    ///         <item>Returns at once with <see cref="ProjectionWaitStatus.Paused" /> or
    ///         <see cref="ProjectionWaitStatus.Rebuilding" /> when a shard that has not reached the position is in
    ///         that state, read from the progression row (when the store records it) or from this process's
    ///         daemon.</item>
    ///         <item>A zero timeout reads once and answers.</item>
    ///         <item>Cancelling <paramref name="token" /> throws <see cref="OperationCanceledException" />, because
    ///         that is the caller giving up, not the projection being behind.</item>
    ///         <item>When the position has a tenant, a shard's tenant row is read if there is one, and otherwise its
    ///         store-wide row. A store that partitions events by tenant keeps one row per tenant, each in that
    ///         tenant's own sequence.</item>
    ///     </list>
    /// </remarks>
    /// <param name="database">The database the position belongs to.</param>
    /// <param name="position">The position to wait for, normally the session's <c>LastAppendedSequence</c>.</param>
    /// <param name="shards">The registered shards to wait on, at their current versions.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="token">The caller's cancellation.</param>
    public static Task<ProjectionWaitResult> WaitAsync(IEventDatabase database, EventStorePosition position,
        IReadOnlyList<ShardName> shards, TimeSpan timeout, CancellationToken token = default)
        => WaitAsync(database, position, shards, timeout, DefaultPollingInterval, token);

    /// <inheritdoc cref="WaitAsync(IEventDatabase, EventStorePosition, IReadOnlyList{ShardName}, TimeSpan, CancellationToken)" />
    /// <param name="pollingInterval">How often to read the progression table.</param>
    public static async Task<ProjectionWaitResult> WaitAsync(IEventDatabase database, EventStorePosition position,
        IReadOnlyList<ShardName> shards, TimeSpan timeout, TimeSpan pollingInterval, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(shards);

        var stopwatch = Stopwatch.StartNew();

        if (shards.Count == 0 || position.Sequence <= 0)
        {
            return new ProjectionWaitResult(ProjectionWaitStatus.Reached, position, [], stopwatch.Elapsed);
        }

        if (timeout <= TimeSpan.Zero)
        {
            var once = await readAsync(database, position, shards, token).ConfigureAwait(false);
            return new ProjectionWaitResult(statusOf(once) ?? ProjectionWaitStatus.TimedOut, position, once,
                stopwatch.Elapsed);
        }

        using var clock = CancellationTokenSource.CreateLinkedTokenSource(token);
        clock.CancelAfter(timeout);

        // Until the first read lands, every shard is unknown
        IReadOnlyList<ProjectionShardWait> last = shards
            .Select(x => new ProjectionShardWait(cellFor(x, position, null, null).Cell, null, false, false, false))
            .ToList();

        try
        {
            while (true)
            {
                last = await readAsync(database, position, shards, clock.Token).ConfigureAwait(false);

                if (statusOf(last) is { } status)
                {
                    return new ProjectionWaitResult(status, position, last, stopwatch.Elapsed);
                }

                await Task.Delay(pollingInterval, clock.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (clock.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // The clock ran out, whatever the provider chose to throw for a cancelled command (fisher#7)
            return new ProjectionWaitResult(ProjectionWaitStatus.TimedOut, position, last, stopwatch.Elapsed);
        }
    }

    /// <summary>
    ///     The registered shards belonging to <paramref name="projectionNames" />, matched on
    ///     <see cref="ShardName.Name" /> without regard to case. Every slice of a sliced projection is included.
    /// </summary>
    /// <exception cref="ArgumentException">A name matches no registered asynchronous projection or subscription.</exception>
    public static IReadOnlyList<ShardName> ShardsNamed(IReadOnlyList<ShardName> registered,
        IReadOnlyList<string> projectionNames)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(projectionNames);

        var shards = new List<ShardName>();
        foreach (var projectionName in projectionNames)
        {
            var matches = registered
                .Where(x => string.Equals(x.Name, projectionName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0)
            {
                throw new ArgumentException(
                    $"'{projectionName}' is not a registered asynchronous projection or subscription, so there is nothing to wait for. "
                    + "An inline projection is current as soon as the commit is. Registered: "
                    + $"[{string.Join(", ", registered.Select(x => x.Name).Distinct())}]",
                    nameof(projectionNames));
            }

            shards.AddRange(matches.Where(x => !shards.Contains(x)));
        }

        return shards;
    }

    /// <summary>
    ///     The registered asynchronous shards of every projection or subscription that is, or publishes, one of
    ///     <paramref name="projections" />, through <see cref="IEventStore.AsyncShardNamesFor" />.
    /// </summary>
    /// <exception cref="ArgumentException">A type matches no asynchronous projection or subscription.</exception>
    public static IReadOnlyList<ShardName> ShardsFor(IEventStore store, IReadOnlyList<Type> projections)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projections);

        var shards = new List<ShardName>();
        foreach (var type in projections)
        {
            var matches = store.AsyncShardNamesFor(type);
            if (matches.Count == 0)
            {
                throw new ArgumentException(
                    $"No asynchronous projection or subscription is, or publishes, {type.FullName}, so there is nothing to wait for. "
                    + "An inline projection is current as soon as the commit is.",
                    nameof(projections));
            }

            shards.AddRange(matches.Where(x => !shards.Contains(x)));
        }

        return shards;
    }

    /// <summary>
    ///     The database <paramref name="position" /> belongs to: the tenant's database when the position has a
    ///     tenant and the store can find it, otherwise the database named by
    ///     <see cref="EventStorePosition.DatabaseIdentifier" />, otherwise the store's only database.
    /// </summary>
    /// <exception cref="ArgumentException">The store has no such database.</exception>
    public static async Task<IEventDatabase> FindDatabaseAsync(IEventStore store, EventStorePosition position,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(position);

        // The tenant lookup first, because it is the cheap one on a store with many databases
        if (position.TenantId != null)
        {
            var forTenant = await store.FindDatabaseForTenantAsync(position.TenantId, token).ConfigureAwait(false);
            if (forTenant != null && (position.DatabaseIdentifier == null || matches(forTenant, position)))
            {
                return forTenant;
            }
        }

        var databases = await store.AllDatabases().ConfigureAwait(false);

        if (position.DatabaseIdentifier != null)
        {
            var named = databases.FirstOrDefault(x => matches(x, position));
            if (named != null) return named;
        }
        else if (databases.Count == 1)
        {
            return databases[0];
        }

        throw new ArgumentException(
            $"Cannot find the database for event store position {position} among [{string.Join(", ", databases.Select(x => x.Identifier))}].",
            nameof(position));

        static bool matches(IEventDatabase database, EventStorePosition position)
            => string.Equals(database.Identifier, position.DatabaseIdentifier, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectionWaitStatus? statusOf(IReadOnlyList<ProjectionShardWait> shards)
    {
        if (shards.All(x => x.Reached)) return ProjectionWaitStatus.Reached;
        if (shards.Any(x => !x.Reached && x.Rebuilding)) return ProjectionWaitStatus.Rebuilding;
        if (shards.Any(x => !x.Reached && x.Paused)) return ProjectionWaitStatus.Paused;

        return null;
    }

    private static async Task<IReadOnlyList<ProjectionShardWait>> readAsync(IEventDatabase database,
        EventStorePosition position, IReadOnlyList<ShardName> shards, CancellationToken token)
    {
        var progress = await database.AllProjectionProgress(token).ConfigureAwait(false);

        var rows = new Dictionary<string, ShardState>();
        var tenanted = new HashSet<string>();

        foreach (var state in progress)
        {
            if (!ShardName.TryParse(state.ShardName, out var parsed) || parsed == null || parsed.IsHighWaterMark)
            {
                continue;
            }

            if (!rows.TryGetValue(parsed.Identity, out var existing) || existing.Sequence < state.Sequence)
            {
                rows[parsed.Identity] = state;
            }

            if (parsed.TenantId != null)
            {
                tenanted.Add(parsed.ForTenant(null).Identity);
            }
        }

        var tracker = trackerOf(database);

        var results = new List<ProjectionShardWait>(shards.Count);
        foreach (var shard in shards)
        {
            var (cell, row) = cellFor(shard, position, rows, tenanted);

            ShardState? live = null;
            tracker?.TryGetCurrentState(cell.Identity, out live);

            var reached = row != null && row.Sequence >= position.Sequence;
            var paused = isPaused(row) || isPaused(live) || live?.Action == ShardAction.Paused;
            var rebuilding = row?.Mode == ShardMode.rebuilding || live?.Mode == ShardMode.rebuilding;

            results.Add(new ProjectionShardWait(cell, row?.Sequence, reached, paused, rebuilding));
        }

        return results;
    }

    private static (ShardName Cell, ShardState? Row) cellFor(ShardName shard, EventStorePosition position,
        IReadOnlyDictionary<string, ShardState>? rows, IReadOnlySet<string>? tenanted)
    {
        var storeWide = shard.TenantId == null ? shard : shard.ForTenant(null);
        ShardState? row = null;

        if (position.TenantId == null)
        {
            rows?.TryGetValue(storeWide.Identity, out row);
            return (storeWide, row);
        }

        var tenantCell = storeWide.ForTenant(position.TenantId);
        if (rows != null && rows.TryGetValue(tenantCell.Identity, out row)) return (tenantCell, row);
        if (rows != null && rows.TryGetValue(storeWide.Identity, out row)) return (storeWide, row);

        // Other tenants have rows and this one does not yet: it is this tenant's row that will appear
        return tenanted != null && tenanted.Contains(storeWide.Identity) ? (tenantCell, null) : (storeWide, null);
    }

    private static bool isPaused(ShardState? state)
        => string.Equals(state?.AgentStatus, "Paused", StringComparison.OrdinalIgnoreCase);

    private static ShardStateTracker? trackerOf(IEventDatabase database)
    {
        try
        {
            return database.Tracker;
        }
        catch (Exception)
        {
            // Not every database has a daemon in this process; the progression table is the answer then
            return null;
        }
    }
}
