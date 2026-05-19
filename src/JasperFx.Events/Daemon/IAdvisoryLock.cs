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
}
