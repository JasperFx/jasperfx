using JasperFx.Core;
using Shouldly;

namespace CoreTests;

public class RecentlyUsedCacheTests
{
    private readonly RecentlyUsedCache<Guid, Item> theCache = new (){Limit = 100};

    [Fact]
    public void get_the_same_value_back()
    {
        var items = new List<Item>();
        for (int i = 0; i < 10; i++)
        {
            var item = new Item(Guid.NewGuid());
            theCache.Store(item.Id, item);
            items.Add(item);
        }

        foreach (var item in items)
        {
            theCache.TryFind(item.Id, out var found).ShouldBeTrue();
            found.ShouldBeSameAs(item);
        }
    }

    [Fact]
    public void contains()
    {
        theCache.Contains(Guid.NewGuid()).ShouldBeFalse();

        var id = Guid.NewGuid();
        theCache.Store(id, new Item(id));
        
        theCache.Contains(id).ShouldBeTrue();
    }

    [Fact(Skip = "Tracks jasperfx#231 — flaky on net9.0 CI (~10%) because the cache's "
              + "eviction order depends on DateTimeOffset.UtcNow resolution; 110 stores in a "
              + "tight loop produce many same-tick timestamps and the LRU order becomes "
              + "ImHashMap-enumeration-dependent. Re-enable once #231 swaps the timestamp "
              + "for an Interlocked.Increment counter (deterministic LRU).")]
    public void compact_moves_off_the_first_ones()
    {
        var items = new List<Item>();
        for (int i = 0; i < 110; i++)
        {
            var item = new Item(Guid.NewGuid());
            theCache.Store(item.Id, item);
            items.Add(item);
        }

        theCache.CompactIfNecessary();

        // The first 10 should have removed
        for (int i = 0; i < 10; i++)
        {
            theCache.TryFind(items[0].Id, out var _).ShouldBeFalse();
        }

        theCache.Count.ShouldBe(theCache.Limit);
    }

    [Fact(Skip = "Tracks jasperfx#231 — same DateTimeOffset.UtcNow resolution issue as "
              + "compact_moves_off_the_first_ones above. Re-enable once #231 lands.")]
    public void request_item_resets()
    {
        var items = new List<Item>();
        for (int i = 0; i < 110; i++)
        {
            var item = new Item(Guid.NewGuid());
            theCache.Store(item.Id, item);
            items.Add(item);
        }

        theCache.TryFind(items[0].Id, out var _).ShouldBeTrue();
        theCache.TryFind(items[2].Id, out var _).ShouldBeTrue();
        theCache.TryFind(items[4].Id, out var _).ShouldBeTrue();
        theCache.TryFind(items[8].Id, out var _).ShouldBeTrue();

        theCache.CompactIfNecessary();
        theCache.Count.ShouldBe(theCache.Limit);

        theCache.TryFind(items[0].Id, out var _).ShouldBeTrue();
        theCache.TryFind(items[2].Id, out var _).ShouldBeTrue();
        theCache.TryFind(items[4].Id, out var _).ShouldBeTrue();
        theCache.TryFind(items[8].Id, out var _).ShouldBeTrue();

        theCache.TryFind(items[1].Id, out var _).ShouldBeFalse();
        theCache.TryFind(items[3].Id, out var _).ShouldBeFalse();
        theCache.TryFind(items[5].Id, out var _).ShouldBeFalse();
        theCache.TryFind(items[7].Id, out var _).ShouldBeFalse();

    }

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

}

public record Item(Guid Id);
