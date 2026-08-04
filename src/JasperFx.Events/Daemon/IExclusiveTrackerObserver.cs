namespace JasperFx.Events.Daemon;

/// <summary>
/// Marks a <see cref="ShardStateTracker"/> observer that is supposed to be the ONLY one of its
/// <see cref="Role"/> attached to a given tracker, because it has an external side effect —
/// typically a database write — that a second instance would simply duplicate.
///
/// <para>
/// The tracker does not enforce this; it logs a warning when a duplicate attaches. Enforcement
/// would be wrong: the second observer is not itself the bug, it is the symptom of a lifecycle bug
/// somewhere upstream (a daemon started twice for one database), and refusing the subscription
/// would hide that rather than surface it.
/// </para>
///
/// <para>
/// This exists because the failure it detects became SILENT. Duplicate
/// <see cref="ExtendedProgressionWriter"/>s used to announce themselves as lock contention — two
/// writers issuing multi-row UPDATEs over the same rows in plan-dependent order is a deadlock
/// hazard. Since marten#5167 those writes are one row per transaction in shard-name order, which
/// makes a duplicate writer harmless to correctness and therefore invisible: it just quietly does
/// the same work twice on a second connection. Making it say so is cheaper than re-deriving it
/// from a wait graph later.
/// </para>
/// </summary>
internal interface IExclusiveTrackerObserver
{
    /// <summary>
    /// Identifies the kind of observer, so two instances of the same role can be recognized as
    /// duplicates. Used in the warning text, so it should read as a noun phrase.
    /// </summary>
    string Role { get; }
}
