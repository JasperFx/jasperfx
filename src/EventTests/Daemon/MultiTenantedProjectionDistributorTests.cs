using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

public class MultiTenantedProjectionDistributorTests
{
    [Fact]
    public async Task build_distribution_emits_one_set_per_database_with_all_shards()
    {
        var dbA = new FakeProjectionDatabase("a");
        var dbB = new FakeProjectionDatabase("b");
        var shards = new[] { new ShardName("Trip", "All", 1), new ShardName("Day", "All", 1) };

        var distributor = new MultiTenantedProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { dbA, dbB }),
            allShards: () => shards,
            lockFactory: _ => new FakeAdvisoryLock(),
            setFactory: (db, names, lockId) => new FakeProjectionSet(db, names, lockId),
            baseLockId: 7);

        var sets = await distributor.BuildDistributionAsync();

        sets.Count.ShouldBe(2);
        // Order is randomized intentionally (BuildDistributionAsync shuffles to spread
        // lock-acquisition contention across nodes), so compare on the sorted projection.
        sets.Select(s => s.Database.Identifier).OrderBy(x => x).ToList().ShouldBe(["a", "b"]);
        foreach (var set in sets)
        {
            set.Names.ShouldBe(shards);
            set.LockId.ShouldBe(7);
        }
    }

    [Fact]
    public async Task try_attain_lock_caches_one_advisory_lock_per_database()
    {
        var db = new FakeProjectionDatabase("only");
        var built = 0;
        var distributor = new MultiTenantedProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { db }),
            allShards: () => Array.Empty<ShardName>(),
            lockFactory: _ =>
            {
                built++;
                return new FakeAdvisoryLock();
            },
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            baseLockId: 0);

        var set = (await distributor.BuildDistributionAsync())[0];

        await distributor.TryAttainLockAsync(set, CancellationToken.None);
        await distributor.TryAttainLockAsync(set, CancellationToken.None);
        await distributor.ReleaseLockAsync(set);

        built.ShouldBe(1);
    }

    [Fact]
    public async Task has_lock_routes_through_cached_advisory_lock()
    {
        var db = new FakeProjectionDatabase("only");
        FakeAdvisoryLock cachedLock = null!;

        var distributor = new MultiTenantedProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { db }),
            allShards: () => Array.Empty<ShardName>(),
            lockFactory: _ =>
            {
                cachedLock = new FakeAdvisoryLock();
                return cachedLock;
            },
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            baseLockId: 42);

        var set = (await distributor.BuildDistributionAsync())[0];

        // Before any acquire attempt, no lock has been built — HasLock should return false safely.
        distributor.HasLock(set).ShouldBeFalse();

        await distributor.TryAttainLockAsync(set, CancellationToken.None);
        cachedLock.ShouldNotBeNull();
        cachedLock!.HeldIds.Add(set.LockId);
        distributor.HasLock(set).ShouldBeTrue();
    }

    [Fact]
    public async Task release_all_locks_disposes_every_cached_lock()
    {
        var dbA = new FakeProjectionDatabase("a");
        var dbB = new FakeProjectionDatabase("b");
        var locks = new List<FakeAdvisoryLock>();
        var distributor = new MultiTenantedProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { dbA, dbB }),
            allShards: () => Array.Empty<ShardName>(),
            lockFactory: _ =>
            {
                var l = new FakeAdvisoryLock();
                locks.Add(l);
                return l;
            },
            setFactory: (db, names, lockId) => new FakeProjectionSet(db, names, lockId),
            baseLockId: 0);

        foreach (var set in await distributor.BuildDistributionAsync())
        {
            await distributor.TryAttainLockAsync(set, CancellationToken.None);
        }

        await distributor.ReleaseAllLocks();

        locks.Count.ShouldBe(2);
        locks.ShouldAllBe(l => l.Disposed);
    }

    [Fact]
    public async Task release_all_locks_swallows_dispose_exceptions()
    {
        // Shutdown-resilience contract carried over from Marten's source — advisory
        // locks can hang in test harnesses; a best-effort release must complete.
        var db = new FakeProjectionDatabase("only");
        var distributor = new MultiTenantedProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { db }),
            allShards: () => Array.Empty<ShardName>(),
            lockFactory: _ => new FakeAdvisoryLock { ThrowOnDispose = true },
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            baseLockId: 0);

        var set = (await distributor.BuildDistributionAsync())[0];
        await distributor.TryAttainLockAsync(set, CancellationToken.None);

        await Should.NotThrowAsync(distributor.ReleaseAllLocks());
    }

    [Fact]
    public async Task dispose_async_releases_all_locks()
    {
        var db = new FakeProjectionDatabase("only");
        var disposed = false;
        var distributor = new MultiTenantedProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { db }),
            allShards: () => Array.Empty<ShardName>(),
            lockFactory: _ => new FakeAdvisoryLock { OnDispose = () => disposed = true },
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            baseLockId: 0);

        var set = (await distributor.BuildDistributionAsync())[0];
        await distributor.TryAttainLockAsync(set, CancellationToken.None);

        await distributor.DisposeAsync();
        disposed.ShouldBeTrue();
    }

    [Fact]
    public void constructor_rejects_null_required_closures()
    {
        Func<ValueTask<IReadOnlyList<IProjectionDatabase>>> dbs = () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(Array.Empty<IProjectionDatabase>());
        Func<IEnumerable<ShardName>> shards = Array.Empty<ShardName>;
        Func<IProjectionDatabase, IAdvisoryLock> locks = _ => new FakeAdvisoryLock();
        Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> sets =
            (db, names, lockId) => new FakeProjectionSet(db, names, lockId);

        Should.Throw<ArgumentNullException>(() => new MultiTenantedProjectionDistributor(null!, shards, locks, sets, 0));
        Should.Throw<ArgumentNullException>(() => new MultiTenantedProjectionDistributor(dbs, null!, locks, sets, 0));
        Should.Throw<ArgumentNullException>(() => new MultiTenantedProjectionDistributor(dbs, shards, null!, sets, 0));
        Should.Throw<ArgumentNullException>(() => new MultiTenantedProjectionDistributor(dbs, shards, locks, null!, 0));
    }

    private sealed class FakeProjectionDatabase : IProjectionDatabase
    {
        public FakeProjectionDatabase(string identifier)
        {
            Identifier = identifier;
            DatabaseUri = new Uri($"fake://{identifier}");
        }

        public string Identifier { get; }
        public Uri DatabaseUri { get; }
    }

    private sealed class FakeProjectionSet : IProjectionSet
    {
        public FakeProjectionSet(IProjectionDatabase database, IReadOnlyList<ShardName> names, int lockId)
        {
            Database = database;
            Names = names;
            LockId = lockId;
        }

        public int LockId { get; }
        public IProjectionDatabase Database { get; }
        public IReadOnlyList<ShardName> Names { get; }
    }

    private sealed class FakeAdvisoryLock : IAdvisoryLock
    {
        public HashSet<int> HeldIds { get; } = new();
        public bool Disposed { get; private set; }
        public bool ThrowOnDispose { get; init; }
        public Action? OnDispose { get; init; }

        public bool HasLock(int lockId) => HeldIds.Contains(lockId);

        public Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
        {
            HeldIds.Add(lockId);
            return Task.FromResult(true);
        }

        public Task ReleaseLockAsync(int lockId)
        {
            HeldIds.Remove(lockId);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            OnDispose?.Invoke();
            if (ThrowOnDispose) throw new InvalidOperationException("simulated shutdown failure");
            return ValueTask.CompletedTask;
        }
    }
}
