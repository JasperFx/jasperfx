namespace JasperFx.Events;

/// <summary>
///     jasperfx#1215 / #1222: where one tenant of a stored-event rewrite or a projection backfill resumes, and where it
///     stops. A run covers the events after <see cref="ResumeAfter" /> up to and including <see cref="StopAfter" />.
/// </summary>
/// <param name="TenantId">
///     The tenant, under tenant-partitioned events or when a request names tenants. Null for the whole database.
/// </param>
/// <param name="ResumeAfter">The last sequence already finished: the stored cursor, or 0 for a fresh run.</param>
/// <param name="StopAfter">The last sequence this run covers: the stored ceiling.</param>
public sealed record EventRangeCursor(string? TenantId, long ResumeAfter, long StopAfter)
{
    /// <summary>
    ///     Whether nothing is left to do for this tenant.
    /// </summary>
    public bool IsComplete => ResumeAfter >= StopAfter;

    /// <summary>
    ///     Build the cursor for one tenant from the event-range marks under <paramref name="ceilingKey" />: the stored
    ///     ceiling, or <paramref name="captureCeiling" /> inserted if absent, and the stored cursor, or 0.
    /// </summary>
    public static async Task<EventRangeCursor> FromMarksAsync(IEventDatabase database, string ceilingKey,
        string? tenantId, Func<CancellationToken, Task<long>> captureCeiling, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(captureCeiling);

        var ceiling = await database.FetchEventRangeMarkAsync(ceilingKey, tenantId, token).ConfigureAwait(false)
                      ?? await database.TrySetEventRangeMarkAsync(ceilingKey, tenantId,
                          await captureCeiling(token).ConfigureAwait(false), token).ConfigureAwait(false);

        var cursor = await database
            .FetchEventRangeMarkAsync(EventRangeMarks.CursorKey(ceilingKey), tenantId, token)
            .ConfigureAwait(false) ?? 0;

        return new EventRangeCursor(tenantId, cursor, ceiling);
    }
}
