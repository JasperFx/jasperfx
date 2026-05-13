using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Strategy for distributing projection shards across nodes in a deployment.
/// Distributors are responsible for grouping shards into <see cref="IProjectionSet"/>s,
/// acquiring exclusive leadership over a set via a distributed lock, and releasing
/// that lock when work moves to another node.
/// </summary>
/// <remarks>
/// Lifted from Marten's <c>IProjectionDistributor</c>. Three storage-agnostic variants
/// are expected:
/// <list type="bullet">
///   <item>Solo — single node, no locks (used when the application is known not to scale out).</item>
///   <item>Single-tenant — multi-node, single tenant; one lock per shard.</item>
///   <item>Multi-tenanted — multi-node, multi-tenant; locks are shard-aware per database.</item>
/// </list>
/// Lock acquisition itself is storage-specific (Postgres advisory locks, SQL Server
/// application locks, etc.) and is provided by product-specific implementations
/// behind this contract.
/// </remarks>
public interface IProjectionDistributor : IAsyncDisposable
{
    /// <summary>
    /// Build the current view of which projection shards should run where. The
    /// coordinator polls this regularly and asks for new locks accordingly.
    /// </summary>
    ValueTask<IReadOnlyList<IProjectionSet>> BuildDistributionAsync();

    /// <summary>
    /// Apply an initial random jitter before polling, so multiple nodes coming up
    /// at the same time do not collide on identical poll intervals.
    /// </summary>
    Task RandomWait(CancellationToken token);

    /// <summary>
    /// True when this distributor already holds the lock for the given set on this node.
    /// </summary>
    bool HasLock(IProjectionSet set);

    /// <summary>
    /// Attempt to acquire the distributed lock for this set on this node.
    /// Returns false when another node holds the lock.
    /// </summary>
    Task<bool> TryAttainLockAsync(IProjectionSet set, CancellationToken token);

    /// <summary>
    /// Release the lock for this set.
    /// </summary>
    Task ReleaseLockAsync(IProjectionSet set);

    /// <summary>
    /// Release every lock this distributor holds. Used during coordinated shutdown
    /// and pause/resume cycles.
    /// </summary>
    Task ReleaseAllLocks();
}

/// <summary>
/// Helpers for computing the deterministic lock identifiers a multi-node deployment
/// uses to negotiate shard ownership. The hash must be identical across nodes so two
/// nodes asking the lock service for the "Trip:All:1" lock both ask for the same id.
/// </summary>
public static class ProjectionLockIds
{
    /// <summary>
    /// Compute a deterministic lock id from a schema-qualified shard identity plus
    /// a base lock id. Mirrors the hash pattern used by Marten's
    /// <c>SingleTenantProjectionDistributor</c> so Marten and Polecat distributors
    /// negotiate the same identifiers when migrated.
    /// </summary>
    public static int Compute(string schemaQualifier, ShardName shardName, int baseLockId)
    {
        ArgumentNullException.ThrowIfNull(shardName);
        return Math.Abs($"{schemaQualifier}:{shardName.Identity}".GetDeterministicHashCode()) + baseLockId;
    }
}
