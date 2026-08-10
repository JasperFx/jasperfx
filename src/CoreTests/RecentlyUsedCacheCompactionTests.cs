using JasperFx.Core;
using Shouldly;

namespace CoreTests;

/// <summary>
/// Regression coverage for gh-640: RecentlyUsedCache.CompactIfNecessary lost
/// more entries than it should ~0.8% of the time. Root cause: ImTools 4.0.0's
/// ImHashMap.Remove corrupts the tree when applied to a map that has already
/// absorbed a previous Remove — survivors that were never removed become
/// unreachable to TryFind while Count() may still include them. The fix
/// rebuilds both maps from the survivors instead of calling Remove, so every
/// tree is only ever built by AddOrUpdate from Empty.
///
/// These tests are fully deterministic: keys are Guids built from a seeded
/// Random, so the ImHashMap tree shape is identical on every run. The
/// [InlineData] seeds are shapes that provably corrupted under the old
/// Remove-based compaction (found by a 20,000-seed sweep against a pure
/// ImTools 4.0.0 repro of the same insert/remove sequence — ~1% failed).
/// </summary>
public class RecentlyUsedCacheCompactionTests
{
    private static Guid SeededGuid(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static (RecentlyUsedCache<Guid, Item> cache, List<Guid> ids) seededCache(int seed, int count, int limit)
    {
        var rng = new Random(seed);
        var cache = new RecentlyUsedCache<Guid, Item> { Limit = limit };
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = SeededGuid(rng);
            ids.Add(id);
            cache.Store(id, new Item(id));
        }

        // The seeded byte streams used here never produce duplicate Guids,
        // but guard anyway so a future edit can't silently weaken the test.
        ids.Distinct().Count().ShouldBe(count);

        return (cache, ids);
    }

    private static void assertFullyConsistent(RecentlyUsedCache<Guid, Item> cache, List<Guid> ids, ISet<Guid> expectedPresent)
    {
        foreach (var id in ids)
        {
            cache.TryFind(id, out _).ShouldBe(expectedPresent.Contains(id),
                $"key {id} findability disagreed with expectation");
        }

        cache.Count.ShouldBe(expectedPresent.Count);
    }

    // These seeds produced ImHashMap tree shapes that the old Remove-based
    // compaction provably corrupted (count and/or findability wrong).
    [Theory]
    [InlineData(83)]
    [InlineData(214)]
    [InlineData(220)]
    [InlineData(507)]
    [InlineData(596)]
    [InlineData(1220)]
    [InlineData(1563)]
    public void compact_keeps_every_survivor_findable_on_known_hostile_tree_shapes(int seed)
    {
        var (cache, ids) = seededCache(seed, 110, 100);

        cache.CompactIfNecessary();

        // LRU: the 10 oldest (first stored) are evicted, the other 100 survive
        var expected = ids.Skip(10).ToHashSet();
        assertFullyConsistent(cache, ids, expected);
    }

    [Fact]
    public void compact_seeded_sweep_never_loses_survivors()
    {
        // 2,000 deterministic tree shapes. Under the old Remove-based
        // compaction ~1% of these rounds lost extra entries or reported a
        // Count that disagreed with TryFind.
        for (var seed = 0; seed < 2000; seed++)
        {
            var (cache, ids) = seededCache(seed, 110, 100);

            cache.CompactIfNecessary();

            var survivors = ids.Skip(10).Count(id => cache.TryFind(id, out _));
            var ghosts = ids.Take(10).Count(id => cache.TryFind(id, out _));

            survivors.ShouldBe(100, $"seed {seed}: survivors lost");
            ghosts.ShouldBe(0, $"seed {seed}: evicted keys still findable");
            cache.Count.ShouldBe(100, $"seed {seed}: Count disagrees with contents");
        }
    }

    [Fact]
    public void compact_at_exactly_the_limit_is_a_noop()
    {
        var (cache, ids) = seededCache(42, 100, 100);

        cache.CompactIfNecessary();

        assertFullyConsistent(cache, ids, ids.ToHashSet());
    }

    [Fact]
    public void compact_under_the_limit_is_a_noop()
    {
        var (cache, ids) = seededCache(42, 60, 100);

        cache.CompactIfNecessary();

        assertFullyConsistent(cache, ids, ids.ToHashSet());
    }

    [Fact]
    public void compact_one_over_the_limit_removes_exactly_one()
    {
        var (cache, ids) = seededCache(42, 101, 100);

        cache.CompactIfNecessary();

        assertFullyConsistent(cache, ids, ids.Skip(1).ToHashSet());
    }

    [Fact]
    public void repeated_compactions_hold_the_limit_and_keep_survivors()
    {
        var rng = new Random(83);
        var cache = new RecentlyUsedCache<Guid, Item> { Limit = 100 };
        var ids = new List<Guid>();

        // Five waves of overflow + compaction. Each compaction leaves trees
        // that the next round mutates again — exactly the repeated-removal
        // history that corrupted the old implementation.
        for (var wave = 0; wave < 5; wave++)
        {
            for (var i = 0; i < 30; i++)
            {
                var id = SeededGuid(rng);
                ids.Add(id);
                cache.Store(id, new Item(id));
            }

            cache.CompactIfNecessary();

            var expectedCount = Math.Min(ids.Count, 100);
            var expected = ids.Skip(ids.Count - expectedCount).ToHashSet();
            assertFullyConsistent(cache, ids, expected);
        }
    }

    [Fact]
    public void try_remove_then_compact_stays_consistent()
    {
        var (cache, ids) = seededCache(83, 110, 100);

        // Interleave targeted removals with compaction — the mixed
        // removal history is what the old code corrupted.
        cache.TryRemove(ids[50]);
        cache.TryRemove(ids[75]);

        cache.CompactIfNecessary();

        // 108 remain before compaction; the 8 oldest surviving keys go
        var expected = ids.Where((_, i) => i != 50 && i != 75).Skip(8).ToHashSet();
        assertFullyConsistent(cache, ids, expected);
    }

    [Fact]
    public void try_remove_missing_key_is_a_noop()
    {
        var (cache, ids) = seededCache(42, 50, 100);

        cache.TryRemove(Guid.NewGuid());

        assertFullyConsistent(cache, ids, ids.ToHashSet());
    }

    [Fact]
    public void many_sequential_try_removes_never_corrupt()
    {
        var (cache, ids) = seededCache(83, 110, 100);

        // Remove every other key one at a time — under the old code each
        // Remove after the first operated on an already-removed-from tree.
        var removed = new HashSet<Guid>();
        for (var i = 0; i < ids.Count; i += 2)
        {
            cache.TryRemove(ids[i]);
            removed.Add(ids[i]);
        }

        assertFullyConsistent(cache, ids, ids.Where(id => !removed.Contains(id)).ToHashSet());
    }

    [Fact]
    public void concurrent_store_and_compact_stays_consistent()
    {
        // RecentlyUsedCache claims thread safety (#226). Hammer Store and
        // CompactIfNecessary concurrently, then verify the quiesced cache
        // is internally consistent: Count agrees with TryFind for every
        // key ever stored, and the limit holds after a final compaction.
        var cache = new RecentlyUsedCache<Guid, Item> { Limit = 100 };
        const int writerCount = 4;
        const int perWriter = 500;

        var batches = Enumerable.Range(0, writerCount)
            .Select(_ => Enumerable.Range(0, perWriter)
                .Select(_ => Guid.NewGuid()).ToArray())
            .ToArray();

        Parallel.For(0, writerCount + 1, worker =>
        {
            if (worker == writerCount)
            {
                for (var i = 0; i < 200; i++)
                {
                    cache.CompactIfNecessary();
                }
            }
            else
            {
                foreach (var id in batches[worker])
                {
                    cache.Store(id, new Item(id));
                }
            }
        });

        cache.CompactIfNecessary();
        cache.Count.ShouldBe(100);

        var findable = batches.SelectMany(x => x).Count(id => cache.TryFind(id, out _));
        findable.ShouldBe(100);
    }
}
