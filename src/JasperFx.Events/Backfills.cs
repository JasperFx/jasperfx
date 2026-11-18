using JasperFx.Events.Projections;

namespace JasperFx.Events;

/// <summary>
///     jasperfx#1222: one call to <see cref="IEventStore.RunBackfillAsync" /> against one database: the backfill
///     declared as <see cref="BackfillName" /> on the projection <see cref="ProjectionName" />, with a cursor and a
///     ceiling for each tenant it covers.
/// </summary>
/// <remarks>
/// <para>
///     A backfill fills a new member on the documents a projection already wrote, from only the events that produce
///     it, while the projection's new live version keeps running from the backfill's ceiling
///     (<see cref="AsyncOptions.SubscribeFromPriorVersionMark" />). It runs as a Wolverine job, one per database, so
///     a store refuses a <c>Backfill(...)</c> declaration when <see cref="EventRegistry.HasJobHost" /> is false.
/// </para>
/// <para>
///     The store reads only the backfill's own event types and the projection's delete events, from each tenant's
///     cursor up to its ceiling, outside the daemon's subscription pipeline. It writes each page's cursor under
///     <see cref="CursorKey" /> in the same transaction as the page, so a crash never applies an event twice.
/// </para>
/// </remarks>
public sealed class BackfillRun
{
    public BackfillRun(IEventDatabase database, string projectionName, string backfillName,
        IReadOnlyList<EventRangeCursor> cursors)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentException.ThrowIfNullOrEmpty(backfillName);
        ProjectionName = projectionName;
        BackfillName = backfillName;
        Cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
    }

    /// <summary>The database to backfill.</summary>
    public IEventDatabase Database { get; }

    /// <summary>The projection the backfill is declared on, named without its version.</summary>
    public string ProjectionName { get; }

    /// <summary>The name of the backfill.</summary>
    public string BackfillName { get; }

    /// <summary>One cursor per tenant, run in this order. A single null-tenant cursor covers the whole database.</summary>
    public IReadOnlyList<EventRangeCursor> Cursors { get; }

    /// <summary>
    ///     Events read per page, and applied with the page's cursor in one transaction. Default 500.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    ///     Called after each page is committed, with that page's counts and the cursor stored with it.
    /// </summary>
    public Func<BackfillProgress, CancellationToken, Task>? OnPage { get; set; }

    /// <summary>The key the ceilings are stored under, shared with the live version's start.</summary>
    public string CeilingKey => EventRangeMarks.BackfillKey(ProjectionName, BackfillName);

    /// <summary>The key the store writes each page's cursor under, in the page's own transaction.</summary>
    public string CursorKey => EventRangeMarks.CursorKey(CeilingKey);

    /// <summary>
    ///     Build a run from the stored marks for the live shard <paramref name="liveShard" /> of the projection: for
    ///     each tenant (the shard's own tenant when <paramref name="tenants" /> is null or empty), the ceiling captured
    ///     by <see cref="EventRangeMarks.CaptureBackfillCeilingAsync" /> and the stored cursor or 0.
    /// </summary>
    public static async Task<BackfillRun> FromMarksAsync(IEventDatabase database, ShardName liveShard,
        string backfillName, IReadOnlyList<string?>? tenants, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(liveShard);

        var ceilingKey = EventRangeMarks.BackfillKey(liveShard.Name, backfillName);
        IReadOnlyList<string?> covered = tenants is { Count: > 0 } ? tenants : [liveShard.TenantId];

        var cursors = new List<EventRangeCursor>();
        foreach (var tenantId in covered)
        {
            var shard = new ShardName(liveShard.Name, liveShard.ShardKey, liveShard.Version, tenantId);
            cursors.Add(await EventRangeCursor.FromMarksAsync(database, ceilingKey, tenantId,
                t => EventRangeMarks.FindPriorVersionMarkAsync(database, shard, t), token).ConfigureAwait(false));
        }

        return new BackfillRun(database, liveShard.Name, backfillName, cursors);
    }
}

/// <summary>
///     jasperfx#1222: one page of a projection backfill, or the totals of one tenant.
/// </summary>
/// <remarks>
///     <see cref="BackfillRun.OnPage" /> receives the counts of the page alone. The list
///     <see cref="IEventStore.RunBackfillAsync" /> returns holds the totals of each tenant for that call.
/// </remarks>
/// <param name="TenantId">The tenant, or null for the whole database.</param>
/// <param name="Sequence">The cursor after the page: the last sequence finished, or the ceiling once complete.</param>
/// <param name="Visited">Events read: the backfill's own event types and the projection's delete events.</param>
/// <param name="Applied">Events applied to a document that exists and is not deleted.</param>
/// <param name="Completed">Whether every event up to the ceiling has been visited.</param>
public sealed record BackfillProgress(
    string? TenantId,
    long Sequence,
    long Visited,
    long Applied,
    bool Completed);
