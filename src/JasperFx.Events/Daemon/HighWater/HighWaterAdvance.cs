namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
///     jasperfx#1031 — what advancing one high water mark to the latest committed sequence WOULD do,
///     from <see cref="IEventStore.PreviewHighWaterAdvanceAsync" />. Read-only: producing it writes
///     nothing.
/// </summary>
/// <param name="DatabaseIdentifier">The <see cref="IEventDatabase.Identifier" /> the mark belongs to.</param>
/// <param name="TenantId">
///     The tenant whose own mark this is, on a database that keeps one mark per tenant (per-tenant event
///     partitioning). Null for a database's single store-global mark.
/// </param>
/// <param name="CurrentMark">The stored high water mark. Zero when none has been stored yet.</param>
/// <param name="CommittedCeiling">
///     The highest COMMITTED event sequence the mark covers — never a reserved sequence or identity value,
///     which can belong to a transaction still in flight (marten#4953).
/// </param>
/// <param name="MissingSequenceCount">
///     How many sequence numbers above <paramref name="CurrentMark" /> and at or below
///     <paramref name="CommittedCeiling" /> have no committed event. Every one of them is skipped for good by
///     every async projection and subscription on that mark if the advance goes ahead, including any whose
///     append is still in flight and commits later. Zero means the advance loses nothing that exists today.
/// </param>
public sealed record HighWaterMarkPreview(
    string DatabaseIdentifier,
    string? TenantId,
    long CurrentMark,
    long CommittedCeiling,
    long MissingSequenceCount)
{
    /// <summary>
    ///     Whether an advance would move this mark at all. A mark is never moved backwards, so a mark at or
    ///     past the committed ceiling stays where it is.
    /// </summary>
    public bool WouldAdvance => CommittedCeiling > CurrentMark;
}

/// <summary>
///     jasperfx#1031 — what <see cref="IEventStore.AdvanceHighWaterMarkToLatestAsync" /> did to one high
///     water mark.
/// </summary>
/// <param name="DatabaseIdentifier">The <see cref="IEventDatabase.Identifier" /> the mark belongs to.</param>
/// <param name="TenantId">The tenant whose own mark this is, or null for a database's store-global mark.</param>
/// <param name="Outcome">Whether the mark moved, and if not, why not.</param>
/// <param name="PreviousMark">
///     The stored mark before the call. When the outcome is
///     <see cref="HighWaterAdvanceOutcome.RefusedStaleExpectation" /> this is the mark the store actually
///     found, which is what the caller should show before asking again.
/// </param>
/// <param name="NewMark">The stored mark after the call. Equal to <paramref name="PreviousMark" /> unless it advanced.</param>
/// <param name="SkippedSequenceCount">
///     How many sequence numbers between <paramref name="PreviousMark" /> and <paramref name="NewMark" /> had no
///     committed event, and are therefore now skipped for good. Zero unless the mark advanced.
/// </param>
public sealed record HighWaterMarkAdvance(
    string DatabaseIdentifier,
    string? TenantId,
    HighWaterAdvanceOutcome Outcome,
    long PreviousMark,
    long NewMark,
    long SkippedSequenceCount);

public enum HighWaterAdvanceOutcome
{
    /// <summary>The mark moved forward to the committed ceiling.</summary>
    Advanced,

    /// <summary>
    ///     Nothing to do: the mark was already at or past the committed ceiling, or there are no committed
    ///     events. A mark is never moved backwards.
    /// </summary>
    AlreadyAtCeiling,

    /// <summary>
    ///     The caller passed an expected current mark and the stored mark no longer matched it, so nothing was
    ///     written. The stored mark moved after the caller looked — typically the daemon got past a gap on its
    ///     own — and a decision made against the old value should be made again.
    /// </summary>
    RefusedStaleExpectation
}
