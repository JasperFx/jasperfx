using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Multi-node, multi-tenant distributor. One <see cref="IProjectionSet"/> per
/// database — every shard for a given tenant database runs together on whichever
/// node holds that database's lock. Trades per-shard scaling for the simpler
/// invariant that a tenant's projections never split across nodes.
/// </summary>
/// <remarks>
/// Lifted from Marten's and Polecat's near-identical
/// <c>MultiTenantedProjectionDistributor</c> concretes (the only meaningfully
/// store-specific bit was the per-database lock instantiation, now injected via
/// <c>lockFactory</c>). See
/// <see href="https://github.com/JasperFx/jasperfx/issues/316">#316</see>;
/// part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
///
/// Locks are cached one-per-database the first time the database is touched
/// and disposed during <see cref="ReleaseAllLocks"/> / <see cref="DisposeAsync"/>.
/// Dispose exceptions are swallowed during teardown — Marten's source documented
/// this behaviour as a workaround for advisory locks hanging in test harnesses
/// and the carried-over xmldoc preserves that intent.
/// </remarks>
public class MultiTenantedProjectionDistributor : IProjectionDistributor
{
    private readonly Func<ValueTask<IReadOnlyList<IProjectionDatabase>>> _databaseSource;
    private readonly Func<IEnumerable<ShardName>> _allShards;
    private readonly Func<IProjectionDatabase, IAdvisoryLock> _lockFactory;
    private readonly Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> _setFactory;
    private readonly int _baseLockId;
    private readonly bool _distributesAgentsPerTenant;
    private readonly Dictionary<string, IAdvisoryLock> _locks = new();

    /// <summary>
    /// Construct a multi-tenant distributor with store-supplied closures.
    /// </summary>
    /// <param name="databaseSource">
    /// Async accessor returning every database the daemon should run shards against.
    /// </param>
    /// <param name="allShards">
    /// Snapshot accessor for every shard the daemon currently knows about. Re-evaluated
    /// on each <see cref="BuildDistributionAsync"/> call so shard additions after
    /// construction are picked up.
    /// </param>
    /// <param name="lockFactory">
    /// Per-database lock factory. Marten passes a closure that builds
    /// <c>Weasel.Postgresql.AdvisoryLock</c>; Polecat passes one that builds its
    /// SQL Server application-lock equivalent. The returned <see cref="IAdvisoryLock"/>
    /// is cached and reused for the lifetime of this distributor.
    /// </param>
    /// <param name="setFactory">
    /// Factory that produces the store-specific <see cref="IProjectionSet"/> for one
    /// (database, shards, lockId) triple. Same pattern as in
    /// <see cref="SoloProjectionDistributor"/> — lets each store keep its richer
    /// <c>ProjectionSet</c> concrete while sharing this distributor skeleton.
    /// </param>
    /// <param name="baseLockId">
    /// Base lock identifier from the store's projection options. Threaded through to
    /// each produced <see cref="IProjectionSet"/> so <see cref="IProjectionSet.LockId"/>
    /// remains stable across the multi-node negotiation.
    /// </param>
    public MultiTenantedProjectionDistributor(
        Func<ValueTask<IReadOnlyList<IProjectionDatabase>>> databaseSource,
        Func<IEnumerable<ShardName>> allShards,
        Func<IProjectionDatabase, IAdvisoryLock> lockFactory,
        Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> setFactory,
        int baseLockId)
        : this(databaseSource, allShards, lockFactory, setFactory, baseLockId,
            distributesAgentsPerTenant: false)
    {
    }

    /// <summary>
    /// jasperfx#489 overload — the original constructor is kept intact for binary
    /// compatibility with consumers compiled against earlier releases.
    /// </summary>
    /// <param name="distributesAgentsPerTenant">
    /// Pass the store's <see cref="IEventStore.DistributesAgentsPerTenant"/> here.
    /// When true, each database's set expands its store-global shard names into
    /// per-tenant shard names using that database's own tenant list
    /// (<see cref="ICrossTenantRebuildSource"/>) — see <see cref="PerTenantShardExpansion"/>.
    /// Lock granularity is unchanged: still one set/lock per database, so a shard
    /// database's tenant agents all run together on the winning node. False keeps
    /// the pre-#489 behavior byte-for-byte.
    /// </param>
    public MultiTenantedProjectionDistributor(
        Func<ValueTask<IReadOnlyList<IProjectionDatabase>>> databaseSource,
        Func<IEnumerable<ShardName>> allShards,
        Func<IProjectionDatabase, IAdvisoryLock> lockFactory,
        Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> setFactory,
        int baseLockId,
        bool distributesAgentsPerTenant)
    {
        _databaseSource = databaseSource ?? throw new ArgumentNullException(nameof(databaseSource));
        _allShards = allShards ?? throw new ArgumentNullException(nameof(allShards));
        _lockFactory = lockFactory ?? throw new ArgumentNullException(nameof(lockFactory));
        _setFactory = setFactory ?? throw new ArgumentNullException(nameof(setFactory));
        _baseLockId = baseLockId;
        _distributesAgentsPerTenant = distributesAgentsPerTenant;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IProjectionSet>> BuildDistributionAsync()
    {
        var databases = await _databaseSource().ConfigureAwait(false);
        var shards = _allShards().ToList();

        // jasperfx#489: when the store distributes agents per tenant, expand each
        // database's store-global shard names into per-tenant names from that
        // database's own tenant list. The set/lock granularity is unchanged — one
        // set per database — so the winning node runs every tenant agent for that
        // shard database.
        var sets = new List<IProjectionSet>(databases.Count);
        foreach (var db in databases)
        {
            IReadOnlyList<ShardName> names = shards;
            if (_distributesAgentsPerTenant)
            {
                names = await PerTenantShardExpansion.ExpandAsync(db, names).ConfigureAwait(false);
            }

            sets.Add(_setFactory(db, names, _baseLockId));
        }

        // Shuffle databases so multiple nodes coming up at the same time don't
        // collide acquiring locks in the same order — same trick both Marten and
        // Polecat already used.
        return sets.OrderBy(_ => Random.Shared.NextDouble()).ToList();
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
                // Need to swallow shutdown failures — advisory locks can hang in
                // test harnesses, and a best-effort release is the correct
                // behaviour during shutdown. Carried over from Marten's source.
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
