using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

public class SingleTenantProjectionDistributorTests
{
    // Regression guard for #317's acceptance bullet: "asserts the lifted distributor
    // produces lock ids identical to Marten's current formula for a sample
    // (schema, shardName, baseLockId) tuple." Marten's pre-lift formula was:
    //
    //   Math.Abs($"{schema}:{shardName.Identity}".GetDeterministicHashCode()) + baseLockId
    //
    // The lifted distributor routes through ProjectionLockIds.Compute. This test
    // pins the formula equivalence — if either side ever drifts, both downstream
    // stores' lock namespaces shift and existing deployments lose lock continuity.
    [Theory]
    [InlineData("public", "Trip", "All", 1u, 4711)]
    [InlineData("events", "Day", "All", 1u, 0)]
    [InlineData("schema_with_underscore", "Multi", "Tenant", 3u, 1_000_000)]
    public void lock_id_formula_matches_martens_pre_lift_formula(
        string schemaQualifier, string projection, string key, uint version, int baseLockId)
    {
        var shard = new ShardName(projection, key, version);
        var expected = Math.Abs(
            $"{schemaQualifier}:{shard.Identity}".GetDeterministicHashCode())
            + baseLockId;

        ProjectionLockIds.Compute(schemaQualifier, shard, baseLockId).ShouldBe(expected);
    }

    [Fact]
    public async Task build_distribution_emits_one_set_per_shard()
    {
        var db = new FakeProjectionDatabase("only");
        var shards = new[]
        {
            new ShardName("Trip", "All", 1),
            new ShardName("Day", "All", 1),
            new ShardName("Activity", "All", 1)
        };

        var distributor = new SingleTenantProjectionDistributor(
            databaseAccessor: () => db,
            allShards: () => shards,
            lockFactory: _ => new FakeAdvisoryLock(),
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            schemaQualifier: "public",
            baseLockId: 100);

        var sets = await distributor.BuildDistributionAsync();

        sets.Count.ShouldBe(3);
        foreach (var set in sets)
        {
            set.Names.Count.ShouldBe(1);
            set.Database.ShouldBe(db);
        }

        // Each set carries the Marten-canonical lock id for its single shard.
        var byShard = sets.ToDictionary(s => s.Names[0].Identity);
        foreach (var shard in shards)
        {
            byShard[shard.Identity].LockId.ShouldBe(
                ProjectionLockIds.Compute("public", shard, 100));
        }
    }

    [Fact]
    public async Task try_attain_lock_caches_one_advisory_lock_per_database()
    {
        var db = new FakeProjectionDatabase("only");
        var built = 0;
        var distributor = new SingleTenantProjectionDistributor(
            databaseAccessor: () => db,
            allShards: () => new[] { new ShardName("Trip", "All", 1), new ShardName("Day", "All", 1) },
            lockFactory: _ =>
            {
                built++;
                return new FakeAdvisoryLock();
            },
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            schemaQualifier: "public",
            baseLockId: 0);

        foreach (var set in await distributor.BuildDistributionAsync())
        {
            await distributor.TryAttainLockAsync(set, CancellationToken.None);
        }

        // One advisory lock per database, regardless of how many shards.
        built.ShouldBe(1);
    }

    [Fact]
    public async Task release_all_locks_disposes_every_cached_lock_and_swallows_exceptions()
    {
        var db = new FakeProjectionDatabase("only");
        var advisoryLock = new FakeAdvisoryLock { ThrowOnDispose = true };
        var distributor = new SingleTenantProjectionDistributor(
            databaseAccessor: () => db,
            allShards: () => new[] { new ShardName("Trip", "All", 1) },
            lockFactory: _ => advisoryLock,
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            schemaQualifier: "public",
            baseLockId: 0);

        var set = (await distributor.BuildDistributionAsync())[0];
        await distributor.TryAttainLockAsync(set, CancellationToken.None);

        await Should.NotThrowAsync(distributor.ReleaseAllLocks());
        advisoryLock.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task dispose_async_releases_all_locks()
    {
        var db = new FakeProjectionDatabase("only");
        var disposed = false;
        var distributor = new SingleTenantProjectionDistributor(
            databaseAccessor: () => db,
            allShards: () => new[] { new ShardName("Trip", "All", 1) },
            lockFactory: _ => new FakeAdvisoryLock { OnDispose = () => disposed = true },
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            schemaQualifier: "public",
            baseLockId: 0);

        var set = (await distributor.BuildDistributionAsync())[0];
        await distributor.TryAttainLockAsync(set, CancellationToken.None);

        await distributor.DisposeAsync();
        disposed.ShouldBeTrue();
    }

    [Fact]
    public void constructor_rejects_null_required_closures_and_schema()
    {
        Func<IProjectionDatabase> db = () => new FakeProjectionDatabase("x");
        Func<IEnumerable<ShardName>> shards = Array.Empty<ShardName>;
        Func<IProjectionDatabase, IAdvisoryLock> locks = _ => new FakeAdvisoryLock();
        Func<IProjectionDatabase, IReadOnlyList<ShardName>, int, IProjectionSet> sets =
            (database, names, lockId) => new FakeProjectionSet(database, names, lockId);

        Should.Throw<ArgumentNullException>(() => new SingleTenantProjectionDistributor(null!, shards, locks, sets, "public", 0));
        Should.Throw<ArgumentNullException>(() => new SingleTenantProjectionDistributor(db, null!, locks, sets, "public", 0));
        Should.Throw<ArgumentNullException>(() => new SingleTenantProjectionDistributor(db, shards, null!, sets, "public", 0));
        Should.Throw<ArgumentNullException>(() => new SingleTenantProjectionDistributor(db, shards, locks, null!, "public", 0));
        Should.Throw<ArgumentNullException>(() => new SingleTenantProjectionDistributor(db, shards, locks, sets, null!, 0));
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
