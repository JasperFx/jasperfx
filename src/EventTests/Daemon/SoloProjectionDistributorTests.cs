using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

public class SoloProjectionDistributorTests
{
    [Fact]
    public async Task build_distribution_returns_one_set_per_database_with_all_shards()
    {
        var dbA = new FakeProjectionDatabase("a", new Uri("fake://a"));
        var dbB = new FakeProjectionDatabase("b", new Uri("fake://b"));
        var shards = new[]
        {
            new ShardName("Trip", "All", 1),
            new ShardName("Day", "All", 1)
        };

        var distributor = new SoloProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { dbA, dbB }),
            allShards: () => shards,
            setFactory: (db, names, lockId) => new FakeProjectionSet(db, names, lockId),
            baseLockId: 4711);

        var sets = await distributor.BuildDistributionAsync();

        sets.Count.ShouldBe(2);
        sets[0].Database.Identifier.ShouldBe("a");
        sets[1].Database.Identifier.ShouldBe("b");
        sets[0].Names.ShouldBe(shards);
        sets[1].Names.ShouldBe(shards);
        sets[0].LockId.ShouldBe(4711);
    }

    [Fact]
    public async Task build_distribution_reruns_all_shards_closure_each_call()
    {
        var db = new FakeProjectionDatabase("only", new Uri("fake://only"));
        var shards = new List<ShardName> { new("Trip", "All", 1) };

        var distributor = new SoloProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(new IProjectionDatabase[] { db }),
            allShards: () => shards,
            setFactory: (database, names, lockId) => new FakeProjectionSet(database, names, lockId),
            baseLockId: 1);

        (await distributor.BuildDistributionAsync())[0].Names.Count.ShouldBe(1);

        // Adding a shard after construction is picked up — the closure is re-evaluated.
        shards.Add(new ShardName("Day", "All", 1));
        (await distributor.BuildDistributionAsync())[0].Names.Count.ShouldBe(2);
    }

    [Fact]
    public async Task no_locks_in_solo_mode()
    {
        var set = new FakeProjectionSet(new FakeProjectionDatabase("a", new Uri("fake://a")), [], 0);
        var distributor = new SoloProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(Array.Empty<IProjectionDatabase>()),
            allShards: () => [],
            setFactory: (_, _, _) => set,
            baseLockId: 0);

        distributor.HasLock(set).ShouldBeTrue();
        (await distributor.TryAttainLockAsync(set, CancellationToken.None)).ShouldBeTrue();
        await Should.NotThrowAsync(distributor.RandomWait(CancellationToken.None));
        await Should.NotThrowAsync(distributor.ReleaseLockAsync(set));
        await Should.NotThrowAsync(distributor.ReleaseAllLocks());
        await Should.NotThrowAsync(async () => await distributor.DisposeAsync());
    }

    [Fact]
    public void constructor_rejects_null_required_closures()
    {
        Should.Throw<ArgumentNullException>(() => new SoloProjectionDistributor(
            databaseSource: null!,
            allShards: () => [],
            setFactory: (_, _, _) => null!,
            baseLockId: 0));

        Should.Throw<ArgumentNullException>(() => new SoloProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(Array.Empty<IProjectionDatabase>()),
            allShards: null!,
            setFactory: (_, _, _) => null!,
            baseLockId: 0));

        Should.Throw<ArgumentNullException>(() => new SoloProjectionDistributor(
            databaseSource: () => new ValueTask<IReadOnlyList<IProjectionDatabase>>(Array.Empty<IProjectionDatabase>()),
            allShards: () => [],
            setFactory: null!,
            baseLockId: 0));
    }

    private sealed class FakeProjectionDatabase : IProjectionDatabase
    {
        public FakeProjectionDatabase(string identifier, Uri uri)
        {
            Identifier = identifier;
            DatabaseUri = uri;
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
}
