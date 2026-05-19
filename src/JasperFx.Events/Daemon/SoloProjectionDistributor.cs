using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Single-node distributor — every set is owned by this node, no locks. Use when
/// the application is known not to scale out (single-process deployments, local
/// dev / test setups, sidecars where lock coordination is delegated upstream).
/// </summary>
/// <remarks>
/// Lifted from Marten's <c>SoloProjectionDistributor</c> and Polecat's near-
/// identical clone (the only difference between the two was the database-source
/// accessor path). Each store wires this concrete with closures over its own
/// database accessor and a factory that produces the store-specific
/// <see cref="IProjectionSet"/> implementation. Tracked under
/// <see href="https://github.com/JasperFx/jasperfx/issues/315">#315</see>; part
/// of the Critter Stack 2026 dedupe pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
///
/// All lock-coordination methods are no-ops: there is no real distributed lock
/// since only one node ever runs. Callers should not depend on lock acquisition
/// failing in this mode — by construction, <see cref="HasLock"/> and
/// <see cref="TryAttainLockAsync"/> always succeed.
/// </remarks>
public sealed class SoloProjectionDistributor : IProjectionDistributor
{
    private readonly Func<ValueTask<IReadOnlyList<IProjectionDatabase>>> _databaseSource;
    private readonly Func<IEnumerable<ShardName>> _allShards;
    private readonly Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> _setFactory;
    private readonly int _baseLockId;

    /// <summary>
    /// Construct a Solo distributor with store-supplied closures.
    /// </summary>
    /// <param name="databaseSource">
    /// Async accessor returning every database the daemon should run shards against.
    /// Typically wraps the store's tenancy / database collection.
    /// </param>
    /// <param name="allShards">
    /// Snapshot accessor for every shard the daemon currently knows about. Re-evaluated
    /// on each <see cref="BuildDistributionAsync"/> call so shard additions registered
    /// after construction are picked up.
    /// </param>
    /// <param name="setFactory">
    /// Factory that produces the store-specific <see cref="IProjectionSet"/> for one
    /// (database, shards, lockId) triple. Lets each store keep its richer
    /// <c>ProjectionSet</c> concrete (with <c>BuildDaemon()</c> / store back-reference
    /// hooks) while sharing this distributor skeleton.
    /// </param>
    /// <param name="baseLockId">
    /// Base lock identifier from the store's projection options. Solo mode never
    /// actually takes a lock, but the value is still threaded through to the
    /// produced <see cref="IProjectionSet"/>s so they expose the same
    /// <see cref="IProjectionSet.LockId"/> a multi-node distributor would.
    /// </param>
    public SoloProjectionDistributor(
        Func<ValueTask<IReadOnlyList<IProjectionDatabase>>> databaseSource,
        Func<IEnumerable<ShardName>> allShards,
        Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> setFactory,
        int baseLockId)
    {
        _databaseSource = databaseSource ?? throw new ArgumentNullException(nameof(databaseSource));
        _allShards = allShards ?? throw new ArgumentNullException(nameof(allShards));
        _setFactory = setFactory ?? throw new ArgumentNullException(nameof(setFactory));
        _baseLockId = baseLockId;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IProjectionSet>> BuildDistributionAsync()
    {
        var databases = await _databaseSource().ConfigureAwait(false);
        var shards = _allShards().ToList();
        return databases.Select(db => _setFactory(db, shards, _baseLockId)).ToList();
    }

    /// <inheritdoc />
    public Task RandomWait(CancellationToken token) => Task.CompletedTask;

    /// <inheritdoc />
    public bool HasLock(IProjectionSet set) => true;

    /// <inheritdoc />
    public Task<bool> TryAttainLockAsync(IProjectionSet set, CancellationToken token)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task ReleaseLockAsync(IProjectionSet set) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ReleaseAllLocks() => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
