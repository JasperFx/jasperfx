using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Multi-node, single-tenant distributor. One <see cref="IProjectionSet"/> per
/// shard — per-shard lock granularity, so different shards can be picked up by
/// different nodes in parallel. Trades the simpler "tenant on a single node"
/// invariant for finer-grained scaling.
/// </summary>
/// <remarks>
/// Lifted from Marten's and Polecat's near-identical <c>SingleTenantProjectionDistributor</c>
/// concretes. Skeleton, lifecycle, and lock dispatch were already byte-identical;
/// the only meaningful divergence was the deterministic lock-id formula, which is
/// resolved by routing both stores through
/// <see cref="ProjectionLockIds.Compute"/> (Marten's formula is canonical —
/// Polecat aligns to it via <see href="https://github.com/JasperFx/polecat/issues/117">polecat#117</see>).
/// See <see href="https://github.com/JasperFx/jasperfx/issues/317">#317</see>;
/// part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
///
/// Shares the per-database advisory-lock caching and shutdown-resilience pattern
/// with <see cref="MultiTenantedProjectionDistributor"/>. The two distributors
/// differ only in distribution granularity: SingleTenant emits one
/// <see cref="IProjectionSet"/> per shard, MultiTenanted emits one per database.
/// </remarks>
public class SingleTenantProjectionDistributor : IProjectionDistributor
{
    private readonly Func<IProjectionDatabase> _databaseAccessor;
    private readonly Func<IEnumerable<ShardName>> _allShards;
    private readonly Func<IProjectionDatabase, IAdvisoryLock> _lockFactory;
    private readonly Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> _setFactory;
    private readonly string _schemaQualifier;
    private readonly int _baseLockId;
    private readonly Dictionary<string, IAdvisoryLock> _locks = new();

    /// <summary>
    /// Construct a single-tenant distributor with store-supplied closures.
    /// </summary>
    /// <param name="databaseAccessor">
    /// Accessor returning the single database this daemon runs against. Single-tenant
    /// deployments expose exactly one database; the closure shape (rather than a
    /// captured value) lets the consumption site lazily resolve from the store's
    /// tenancy after construction.
    /// </param>
    /// <param name="allShards">
    /// Snapshot accessor for every shard the daemon currently knows about. Re-evaluated
    /// on each <see cref="BuildDistributionAsync"/> call so shards added after
    /// construction are picked up.
    /// </param>
    /// <param name="lockFactory">
    /// Per-database lock factory. See
    /// <see cref="MultiTenantedProjectionDistributor"/> for the same closure shape.
    /// </param>
    /// <param name="setFactory">
    /// Factory that produces the store-specific <see cref="IProjectionSet"/> for one
    /// (database, [single-shard], computed-lockId) triple.
    /// </param>
    /// <param name="schemaQualifier">
    /// Schema-name prefix mixed into <see cref="ProjectionLockIds.Compute"/> so the
    /// computed lock identifier is stable across nodes (every node mixes in the
    /// same schema name + shard identity). Marten threads its
    /// <c>EventGraph.DatabaseSchemaName</c> here.
    /// </param>
    /// <param name="baseLockId">
    /// Base lock identifier from the store's projection options. Added to the
    /// schema-+-shard hash so every produced shard set gets a stable but
    /// deployment-isolated lock id.
    /// </param>
    public SingleTenantProjectionDistributor(
        Func<IProjectionDatabase> databaseAccessor,
        Func<IEnumerable<ShardName>> allShards,
        Func<IProjectionDatabase, IAdvisoryLock> lockFactory,
        Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> setFactory,
        string schemaQualifier,
        int baseLockId)
    {
        _databaseAccessor = databaseAccessor ?? throw new ArgumentNullException(nameof(databaseAccessor));
        _allShards = allShards ?? throw new ArgumentNullException(nameof(allShards));
        _lockFactory = lockFactory ?? throw new ArgumentNullException(nameof(lockFactory));
        _setFactory = setFactory ?? throw new ArgumentNullException(nameof(setFactory));
        _schemaQualifier = schemaQualifier ?? throw new ArgumentNullException(nameof(schemaQualifier));
        _baseLockId = baseLockId;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IProjectionSet>> BuildDistributionAsync()
    {
        var database = _databaseAccessor();

        // One IProjectionSet per shard — per-shard lock granularity. Shuffle the
        // result so multiple nodes coming up at the same time don't race for locks
        // in identical order (same trick MultiTenanted uses, same trick both
        // Marten and Polecat already shipped).
        IReadOnlyList<IProjectionSet> sets = _allShards()
            .Select(shard => _setFactory(
                database,
                [shard],
                ProjectionLockIds.Compute(_schemaQualifier, shard, _baseLockId)))
            .OrderBy(_ => Random.Shared.NextDouble())
            .ToList();

        return ValueTask.FromResult(sets);
    }

    /// <summary>
    /// Apply a 0–500ms random delay before polling. Virtual so tests can stub
    /// out the wait without touching <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </summary>
    public virtual Task RandomWait(CancellationToken token)
        => Task.Delay(Random.Shared.Next(0, 500).Milliseconds(), token);

    /// <inheritdoc />
    public bool HasLock(IProjectionSet set)
        => _locks.TryGetValue(set.Database.Identifier, out var advisoryLock)
           && advisoryLock.HasLock(set.LockId);

    /// <inheritdoc />
    public Task<bool> TryAttainLockAsync(IProjectionSet set, CancellationToken token)
        => LockFor(set.Database).TryAttainLockAsync(set.LockId, token);

    /// <inheritdoc />
    public Task ReleaseLockAsync(IProjectionSet set)
        => LockFor(set.Database).ReleaseLockAsync(set.LockId);

    /// <inheritdoc />
    public async Task ReleaseAllLocks()
    {
        foreach (var advisoryLock in _locks.Values)
        {
            try
            {
                await advisoryLock.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Swallow shutdown failures — advisory locks can hang in test
                // harnesses, best-effort release is the correct shutdown contract.
                // Carried over from both Marten's and Polecat's source.
            }
        }

        _locks.Clear();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ReleaseAllLocks().ConfigureAwait(false);
    }

    private IAdvisoryLock LockFor(IProjectionDatabase database)
    {
        if (!_locks.TryGetValue(database.Identifier, out var advisoryLock))
        {
            advisoryLock = _lockFactory(database);
            _locks[database.Identifier] = advisoryLock;
        }

        return advisoryLock;
    }
}
