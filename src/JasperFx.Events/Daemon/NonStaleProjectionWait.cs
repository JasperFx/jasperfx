using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     The wait behind <see cref="IEventDatabase.WaitForNonStaleProjectionDataAsync" />: poll one database
///     until every registered shard has reached the event sequence as of the call, or throw
///     <see cref="TimeoutException" />. See jasperfx#1046.
/// </summary>
/// <remarks>
///     <para>
///         Public because a default interface member cannot be reached through a class. A store whose
///         database class keeps a public <c>WaitForNonStaleProjectionDataAsync</c> for its own callers has
///         thereby implemented the interface member, so that method has to forward here: casting to
///         <see cref="IEventDatabase" /> would call itself.
///     </para>
/// </remarks>
public static class NonStaleProjectionWait
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     Wait until every cell of <paramref name="registeredShards" /> is current on
    ///     <paramref name="database" />. <see cref="ProjectionLagCalculator.StaleCells" /> says what current means.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///     <item>The ceiling is read once, on entry. A ceiling re-read on every poll, which is what Polecat and
    ///     Fisher did, is a moving target: under sustained appends the wait never finishes, and it was only ever
    ///     asked about the events that existed when it was called. Marten reads it once.</item>
    ///     <item>A store with no events returns at once, because nothing can be stale. Marten instead waits for
    ///     events to appear and then throws, which callers have had to route around (bobcat#497).</item>
    ///     <item>Every failure after this method's own clock runs out is a <see cref="TimeoutException" />,
    ///     whatever a provider chose to throw. The token reaches both reads, and a provider reports a cancelled
    ///     command in its own way: SqlClient as a bare <c>SqlException</c>, Npgsql as an exception wrapping an
    ///     <see cref="OperationCanceledException" />. Callers catch <see cref="TimeoutException" />, among them
    ///     <see cref="JasperFxAsyncDaemon{TOperations,TQuerySession,TProjection}.WaitForNonStaleData" />, and the
    ///     same condition must not surface as a different type depending on where the clock happened to land
    ///     (fisher#7). A failure before then is a real failure and propagates untouched.</item>
    ///     </list>
    /// </remarks>
    /// <param name="database">The database to read.</param>
    /// <param name="registeredShards">
    ///     The shards the application runs on this database, not the rows that happen to exist: a shard that has
    ///     not written a row yet is behind, and reading the answer off the rows cannot see it (fisher#102,
    ///     polecat#602).
    /// </param>
    /// <param name="timeout">How long to wait for.</param>
    /// <exception cref="TimeoutException">
    ///     The shards had not caught up in time. The message separates the shards that are behind from those
    ///     that never reported, because "still catching up" and "never started" are different situations.
    /// </exception>
    public static async Task WaitAsync(IEventDatabase database, IReadOnlyList<ShardName> registeredShards,
        TimeSpan timeout)
    {
        // Nothing runs asynchronously, so there is nothing that could be stale
        if (registeredShards.Count == 0) return;

        using var cancellation = new CancellationTokenSource(timeout);

        var ceiling = 0L;
        IReadOnlyList<ProjectionLag> stale = [];

        try
        {
            ceiling = await database.FetchHighestEventSequenceNumber(cancellation.Token).ConfigureAwait(false);
            if (ceiling == 0) return;

            while (true)
            {
                var progress = await database.AllProjectionProgress(cancellation.Token).ConfigureAwait(false);
                var lags = ProjectionLagCalculator.Calculate(registeredShards, progress, database.Identifier);

                stale = ProjectionLagCalculator.StaleCells(lags, ceiling);
                if (stale.Count == 0) return;

                await Task.Delay(PollingInterval, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(describe(database, timeout, ceiling, stale), e);
        }
    }

    private static string describe(IEventDatabase database, TimeSpan timeout, long ceiling,
        IReadOnlyList<ProjectionLag> stale)
    {
        if (ceiling == 0)
        {
            return $"The highest event sequence of database '{database.Identifier}' could not be read within the timeout span of {timeout}.";
        }

        if (stale.Count == 0)
        {
            return $"Projection progress on database '{database.Identifier}' could not be read within the timeout span of {timeout}, while waiting for sequence {ceiling}.";
        }

        var behind = stale.Where(x => x.HasProgressionRow).Select(x => $"{x.Shard.Identity}:{x.Sequence}");
        var missing = stale.Where(x => !x.HasProgressionRow).Select(x => x.Shard.Identity).ToArray();

        var detail = missing.Length > 0
            ? $" Registered shards with no progress recorded: [{string.Join(", ", missing)}]."
            : string.Empty;

        return $"Projection data on database '{database.Identifier}' was still stale after {timeout}. "
               + $"Waiting for sequence {ceiling}; shards behind it are at [{string.Join(", ", behind)}]."
               + detail
               + describeAgents(database, stale);
    }

    /// <summary>
    ///     What the daemon in this process last said about each stale shard (fisher#329). A timeout that
    ///     names a shard and nothing else cannot tell "slow" from "stopped", and the tracker holds each agent's
    ///     last state, including its pause reason and exception.
    /// </summary>
    private static string describeAgents(IEventDatabase database, IReadOnlyList<ProjectionLag> stale)
    {
        try
        {
            var tracker = database.Tracker;

            // Not "none of the stale shards": a sibling at the head means the daemon IS running, and the
            // hint would send the reader the wrong way (fisher#329)
            if (tracker.CurrentStates().Count == 0)
            {
                return " No agent has reported any state in this process, so the daemon may not be running.";
            }

            var agents = stale.Select(lag =>
                tracker.TryGetCurrentState(lag.Shard.Identity, out var state)
                    ? describeAgent(state)
                    : $"{lag.Shard.Identity}: no agent has reported any state in this process");

            return $" Agents: [{string.Join("; ", agents)}].";
        }
        catch (Exception)
        {
            // This is the message of the TimeoutException callers are waiting to catch, so nothing about
            // reading the tracker may replace it
            return string.Empty;
        }
    }

    private static string describeAgent(ShardState state)
    {
        var parts = new List<string> { $"{state.ShardName}: last {state.Action} at {state.Sequence}" };

        if (state.AgentStatus is { } status)
        {
            parts.Add($"agent {status}");
        }

        if (state.PauseReason is { } reason)
        {
            parts.Add($"paused: {headline(reason)}");
        }

        if (state.Exception is { } exception)
        {
            parts.Add($"{exception.GetType().Name}: {exception.Message}");
        }
        else if (state.Failure is { } failure)
        {
            parts.Add($"{failure.Category} failure, {failure.ExceptionType}: {failure.Message}");
        }

        return string.Join(", ", parts);
    }

    // A pause reason is the whole exception, frames included, and a timeout carrying one per stale shard is
    // unreadable. The inner lines have to survive: an apply failure arrives wrapped in ApplyEventException,
    // whose own message names the event and not what went wrong with it.
    private static string headline(string exceptionText)
    {
        var lines = exceptionText.Split('\n');

        return string.Join(" ", lines
            .Where((line, index) => index == 0 || line.TrimStart().StartsWith("--->", StringComparison.Ordinal))
            .Select(line => line.Trim()));
    }
}
