using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events;

/// <summary>
///     jasperfx#1215 / #1222: the keys of the event-range marks a stored-event rewrite and a projection backfill keep
///     on <see cref="IEventDatabase" />, and the one way to capture a backfill's ceiling, which a projection's live
///     version and the backfill job both use so they split history at exactly one sequence.
/// </summary>
/// <remarks>
/// <para>
///     A mark is one sequence stored under a string key plus a tenant (null for store-global). The key a rewrite
///     or a backfill is named by holds its ceiling, the last sequence it covers. Its cursor, the last sequence it
///     finished, is a second mark under <see cref="CursorKey" />.
/// </para>
/// <para>
///     A store writes the cursor in the same transaction as the page it covers, so a crash between a page and its
///     cursor cannot apply that page twice. The job that drives the run reads the cursor when it starts.
/// </para>
/// </remarks>
public static class EventRangeMarks
{
    /// <summary>
    ///     The key of a stored-event rewrite's ceiling: <c>rewrite:{name}</c>.
    /// </summary>
    public static string RewriteKey(string rewriteName)
    {
        ArgumentException.ThrowIfNullOrEmpty(rewriteName);
        return $"rewrite:{rewriteName}";
    }

    /// <summary>
    ///     The key of a projection backfill's ceiling: <c>backfill:{projection}:{name}</c>. The projection is named
    ///     without its version.
    /// </summary>
    public static string BackfillKey(string projectionName, string backfillName)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentException.ThrowIfNullOrEmpty(backfillName);
        return $"backfill:{projectionName}:{backfillName}";
    }

    /// <summary>
    ///     The key of the cursor that goes with a ceiling's key: <c>{key}:cursor</c>.
    /// </summary>
    public static string CursorKey(string markKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(markKey);
        return $"{markKey}:cursor";
    }

    /// <summary>
    ///     The sequence a projection's live version and its backfill split history at: the highest prior version's
    ///     persisted progression for the same projection, shard key and tenant. With no prior version on record it
    ///     is the live shard's own progression, which is 0 for a shard that has never run.
    /// </summary>
    /// <remarks>
    ///     Only <see cref="CaptureBackfillCeilingAsync" /> should feed this into a mark. Read alone it moves while the
    ///     prior version is still running, and the split is only exact when both sides use the one stored value.
    /// </remarks>
    public static async Task<long> FindPriorVersionMarkAsync(IEventDatabase database, ShardName liveShard,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(liveShard);

        var prior = await FindPriorVersionProgressAsync(database, liveShard, token).ConfigureAwait(false);
        if (prior.HasValue) return prior.Value;

        return await database.ProjectionProgressFor(liveShard, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Capture the ceiling of a backfill declared on the projection <paramref name="liveShard" /> belongs to,
    ///     once per database and tenant: the stored mark when there is one, otherwise
    ///     <see cref="FindPriorVersionMarkAsync" /> inserted if absent. Returns the stored winner.
    /// </summary>
    /// <remarks>
    ///     <see cref="AsyncOptions.SubscribeFromPriorVersionMark" /> starts the live version from this value, and the
    ///     backfill job stops at it. Whichever of the two gets here first stores it, and the other uses what it
    ///     stored, so no event is covered twice or missed.
    /// </remarks>
    public static async Task<long> CaptureBackfillCeilingAsync(IEventDatabase database, ShardName liveShard,
        string backfillName, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(liveShard);

        var key = BackfillKey(liveShard.Name, backfillName);

        var stored = await database.FetchEventRangeMarkAsync(key, liveShard.TenantId, token).ConfigureAwait(false);
        if (stored.HasValue) return stored.Value;

        var mark = await FindPriorVersionMarkAsync(database, liveShard, token).ConfigureAwait(false);
        return await database.TrySetEventRangeMarkAsync(key, liveShard.TenantId, mark, token).ConfigureAwait(false);
    }

    // jasperfx#480: the HIGHEST prior version's persisted progression for the same (projection, shard key, tenant).
    // The version is part of the progression row's identity (Trips:V2:All vs Trips:V3:All), so the prior mark
    // survives the version bump and parses back out of AllProjectionProgress. Null when no prior version has a row.
    internal static async Task<long?> FindPriorVersionProgressAsync(IEventDatabase database, ShardName name,
        CancellationToken token)
    {
        if (name.Version <= 1) return null;

        var progress = await database.AllProjectionProgress(token).ConfigureAwait(false);

        long? mark = null;
        uint priorVersion = 0;
        foreach (var state in progress)
        {
            if (!ShardName.TryParse(state.ShardName, out var parsed) || parsed == null)
            {
                continue;
            }

            if (parsed.Version >= name.Version || parsed.Version < priorVersion)
            {
                continue;
            }

            if (!parsed.Name.EqualsIgnoreCase(name.Name)) continue;
            if (!parsed.ShardKey.EqualsIgnoreCase(name.ShardKey)) continue;
            if (!string.Equals(parsed.TenantId, name.TenantId)) continue;

            priorVersion = parsed.Version;
            mark = state.Sequence;
        }

        return mark;
    }
}
