namespace JasperFx.Events.Daemon;

/// <summary>
///     Who currently holds a distribution lock, as read back from the store's own lock primitive. Returned
///     by <see cref="IAdvisoryLock.FindHolderAsync" />.
/// </summary>
/// <remarks>
///     <para>
///     jasperfx#913. <see cref="IAdvisoryLock.HasLock" /> answers only "does <em>this</em> node", so under a
///     store's own lock-based distribution (Marten HotCold, Polecat's application locks) nothing could say
///     which node owned a lock set this node does not hold. That is exactly the diagnostic an operator wants
///     when a shard stops with <see cref="ProgressionProgressOutOfOrderException" />, which means two
///     processes believe they own it.
///     </para>
///     <para>
///     Every member past <see cref="LockId" /> is optional, because the two primitives expose different
///     things: Postgres joins <c>pg_locks</c> to <c>pg_stat_activity</c> (pid, <c>application_name</c>,
///     <c>client_addr</c>, backend start), SQL Server reads <c>sys.dm_tran_locks</c> for the app-lock
///     resource. A store fills in what it can see and leaves the rest null; null means "not known", never
///     "empty".
///     </para>
///     <para>
///     Declared with one positional member on purpose — the optional ones are init-only properties, so a
///     later addition is not a breaking change to a positional deconstruction.
///     </para>
/// </remarks>
/// <param name="LockId">
///     The lock id this holder was read for, as computed by <see cref="ProjectionLockIds.Compute" />. Carried
///     on the result so a holder survives being collected out of several reads.
/// </param>
public sealed record AdvisoryLockHolder(int LockId)
{
    /// <summary>
    ///     The application name the holding connection set, where the store's primitive exposes one
    ///     (Postgres <c>application_name</c>, SQL Server <c>program_name</c>). This is the member most likely
    ///     to identify a node usefully, because a host can stamp its own node id into it.
    /// </summary>
    public string? ApplicationName { get; init; }

    /// <summary>
    ///     The store's own identifier for the holding connection, rendered as text — a backend pid on
    ///     Postgres, a session id on SQL Server. Text rather than a number so a store whose primitive
    ///     identifies a holder by something other than an integer does not have to misrepresent it.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    ///     The network address the holding connection came from, where known (Postgres <c>client_addr</c>).
    ///     Null for a local connection as well as for a store that cannot see it, so this is a display aid
    ///     rather than something to branch on.
    /// </summary>
    public string? ClientAddress { get; init; }

    /// <summary>
    ///     When the holding connection started, where known. Not the time the lock was taken — no primitive
    ///     here records that — so treat it as an upper bound on the hold's age.
    /// </summary>
    public DateTimeOffset? HeldSince { get; init; }

    /// <summary>
    ///     Whether the holder is this node, when the implementation can tell (typically by matching
    ///     <see cref="ApplicationName" /> or <see cref="SessionId" /> against its own connection). Null means
    ///     the implementation could not decide, which is deliberately distinct from false: "someone else
    ///     holds it" and "I cannot tell who holds it" lead to different operator conclusions.
    /// </summary>
    public bool? IsCurrentNode { get; init; }
}
