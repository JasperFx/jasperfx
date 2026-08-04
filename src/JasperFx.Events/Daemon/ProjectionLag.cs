using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     How far behind the event stream a single registered projection/subscription cell is, at the
///     version that is registered right now. One value per (shard, tenant) cell.
///     <para>
///     jasperfx#619: the "anchor on registered sources at their current version, and treat a missing
///     row as fully behind rather than caught up" semantic had grown three independent
///     implementations (the daemon's blue/green side-effect gate, Marten's
///     <c>WaitForNonStaleDataAsync</c>, and application code in the field), each of which had to
///     rediscover the same four traps: a store-global high-water bar is wrong when each tenant draws
///     its own sequence, a store-global <c>:All</c> agent records no per-tenant rows at all,
///     non-shard bookkeeping rows never advance and must be excluded, and a sliced projection fans
///     out across shard keys.
///     </para>
/// </summary>
/// <param name="Shard">
///     The cell this lag describes. Carries <see cref="ShardName.Name" />,
///     <see cref="ShardName.ShardKey" />, <see cref="ShardName.Version" /> and
///     <see cref="ShardName.TenantId" />, so a projection sliced across custom shard keys reports one
///     value per slice instead of collapsing them.
/// </param>
/// <param name="DatabaseIdentifier">
///     <see cref="IEventDatabase.Identifier" /> this reading came from. A fan-out across many
///     databases is otherwise an unattributable list — every database publishes the same shard names.
/// </param>
/// <param name="Sequence">
///     The persisted progression for this cell at its CURRENT version, or 0 when there is no row for
///     it (see <paramref name="HasProgressionRow" />). A prior version's row is never borrowed: the
///     version is baked into the progression-row identity, so a version bump reads as "no progress
///     yet", which is what it is.
/// </param>
/// <param name="HighWaterMark">
///     The high-water mark this cell advances against — that tenant's own mark under per-tenant event
///     partitioning, not a store-global one.
/// </param>
/// <param name="HasProgressionRow">
///     Whether a progression row exists for this cell at its current version. A real field rather than
///     a <c>Sequence == 0</c> sentinel: conflating "never started" with "at zero" is exactly what
///     latches a readiness probe green during a version bump while the previous version's row still
///     sits at the old mark.
/// </param>
public readonly record struct ProjectionLag(
    ShardName Shard,
    string? DatabaseIdentifier,
    long Sequence,
    long HighWaterMark,
    bool HasProgressionRow)
{
    /// <summary>
    ///     Events this cell still has to process. Never negative — a shard can legitimately read
    ///     ahead of a stale high-water snapshot.
    /// </summary>
    public long Lag => Math.Max(0, HighWaterMark - Sequence);

    /// <summary>
    ///     True only when this cell actually has a progression row AND that row has reached the mark.
    ///     A cell with no row is NOT caught up, however small the lag arithmetic makes it look.
    /// </summary>
    public bool IsCaughtUp => HasProgressionRow && Sequence >= HighWaterMark;

    public override string ToString()
        => $"{Shard.Identity} @ {Sequence}/{HighWaterMark} (lag {Lag}{(HasProgressionRow ? "" : ", no row")})";
}
