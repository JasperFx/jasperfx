namespace JasperFx.Events.Daemon;

/// <summary>
/// Per-database distributed lock contract consumed by the multi-node projection
/// distributors. Concrete implementations wrap a storage-specific lock primitive
/// (e.g. <c>Weasel.Postgresql.AdvisoryLock</c> on Postgres, an application lock
/// on SQL Server) so the shared
/// <see cref="MultiTenantedProjectionDistributor"/> can route
/// <see cref="IProjectionDistributor.HasLock"/> /
/// <see cref="IProjectionDistributor.TryAttainLockAsync"/> /
/// <see cref="IProjectionDistributor.ReleaseLockAsync"/> through a single
/// abstraction.
/// </summary>
/// <remarks>
/// Lifted from the implicit contract Marten's <c>Weasel.Postgresql.AdvisoryLock</c>
/// and Polecat's <c>SqlServerAppLock</c> both satisfy. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/316">#316</see>.
///
/// All three lock methods are keyed by an <c>int</c> lock id — distributors
/// compute these via <see cref="ProjectionLockIds.Compute"/> so multiple nodes
/// asking for the same logical set negotiate the same identifier.
/// </remarks>
public interface IAdvisoryLock : IAsyncDisposable
{
    /// <summary>
    /// True when this node currently holds the lock identified by <paramref name="lockId"/>.
    /// Implementations should return false for unknown ids rather than throwing.
    /// </summary>
    bool HasLock(int lockId);

    /// <summary>
    /// Attempt to acquire the lock identified by <paramref name="lockId"/>. Returns
    /// false when another node holds it.
    /// </summary>
    Task<bool> TryAttainLockAsync(int lockId, CancellationToken token);

    /// <summary>
    /// Release the lock identified by <paramref name="lockId"/>. Safe to call when
    /// the lock isn't held — implementations are expected to short-circuit.
    /// </summary>
    Task ReleaseLockAsync(int lockId);

    /// <summary>
    /// Read back WHO currently holds the lock identified by <paramref name="lockId"/>, or null when nothing
    /// holds it. See <see href="https://github.com/JasperFx/jasperfx/issues/913">#913</see>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three members above abstract <em>acquiring</em> a lock, and <see cref="HasLock" /> answers only
    /// "does <em>this</em> node". So no node — and no monitoring tool — could learn which node owns a lock
    /// set it does not itself hold. Under Wolverine-managed distribution the assignment table answers that
    /// question; under a store's own lock-based distribution (Marten HotCold via
    /// <c>Weasel.Postgresql.AdvisoryLock</c>, Polecat via <c>SqlServerAppLock</c>) the lock IS the authority
    /// and nothing exposed its holder. It is the same shape as jasperfx#885: the operation was abstracted
    /// and the diagnostic was not.
    /// </para>
    /// <para>
    /// Implementations read their own primitive: <c>pg_locks</c> joined to <c>pg_stat_activity</c> for the
    /// advisory lock key on Postgres, <c>sys.dm_tran_locks</c> for the app-lock resource on SQL Server. Fill
    /// in whichever <see cref="AdvisoryLockHolder" /> members that view exposes and leave the rest null.
    /// </para>
    /// <para>
    /// <b>The default throws <see cref="NotSupportedException" /> rather than returning null</b>, because
    /// null is a meaningful answer here — "nothing holds this lock" is precisely the reassuring conclusion an
    /// operator chasing a double-runner would draw, and a lock implementation that has not implemented the
    /// read must not be able to supply it. An unimplemented read and an unheld lock are different facts, and
    /// this is the seam where they are still distinguishable. For the same reason there is no capability flag
    /// to test first: a store implements one member and cannot get it half right.
    /// </para>
    /// </remarks>
    /// <param name="lockId">
    /// The lock id to read, as computed by <see cref="ProjectionLockIds.Compute" /> — the same identifier the
    /// three acquisition members take.
    /// </param>
    /// <returns>The current holder, or null when the lock is not held by anyone.</returns>
    Task<AdvisoryLockHolder?> FindHolderAsync(int lockId, CancellationToken token)
        => throw new NotSupportedException(
            "FindHolderAsync is not implemented on this IAdvisoryLock, so the holder of this lock cannot be read. Use a lock implementation that can interrogate its own primitive (pg_locks joined to pg_stat_activity on Postgres, sys.dm_tran_locks on SQL Server).");
}
