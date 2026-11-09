using JasperFx.Core;
using JasperFx.Events.Fetching;
using Shouldly;

namespace StressTests;

// Moved from CoreTests' RecentlyUsedCacheTests / RecentlyUsedCacheCompactionTests and EventTests'
// RecentlyUsedAggregateWriteCacheTests.
public class CacheStressTests
{
    [Fact]
    public void concurrent_store_does_not_lose_entries()
    {
        // Regression coverage for #226 — RecentlyUsedCache.Store() was not
        // thread-safe; concurrent Stores raced on the field-level
        // `_items = _items.AddOrUpdate(...)` write and dropped entries.
        // The async daemon's slice processor runs Store with up to 10-way
        // parallelism; with Polecat's CritterWatch upstream-cache scenario
        // (composite projection, tiny cache, multi-stream batch) the lost-
        // update race surfaced as a flaky test (polecat#53).

        var cache = new RecentlyUsedCache<Guid, Item> { Limit = 10000 };
        const int writerCount = 16;
        const int perWriter = 500;

        var items = Enumerable.Range(0, writerCount)
            .Select(_ => Enumerable.Range(0, perWriter)
                .Select(_ => new Item(Guid.NewGuid())).ToArray())
            .ToArray();

        Parallel.For(0, writerCount, writer =>
        {
            foreach (var item in items[writer])
            {
                cache.Store(item.Id, item);
            }
        });

        cache.Count.ShouldBe(writerCount * perWriter);
        foreach (var batch in items)
        {
            foreach (var item in batch)
            {
                cache.TryFind(item.Id, out var found).ShouldBeTrue();
                found.ShouldBeSameAs(item);
            }
        }
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

    [Fact]
    public void exactly_one_of_many_concurrent_takers_wins_an_entry()
    {
        var cache = new RecentlyUsedAggregateWriteCache();
        var key = new AggregateCacheKey(typeof(Basket), "db-1", "*default*", Guid.NewGuid());
        cache.Store(key, new Basket(), 7);

        var winners = 0;
        Parallel.For(0, 64, i =>
        {
            if (cache.TryTake(key, out _, out _))
            {
                Interlocked.Increment(ref winners);
            }
        });

        winners.ShouldBe(1);
    }

    public record Item(Guid Id);

    public class Basket
    {
        public Guid Id { get; set; }
    }
}
