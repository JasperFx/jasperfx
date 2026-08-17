using JasperFx.Events;
using JasperFx.Events.Fetching;
using Shouldly;

namespace EventTests.Fetching;

public class RecentlyUsedAggregateWriteCacheTests
{
    private readonly RecentlyUsedAggregateWriteCache theCache = new();

    private static AggregateCacheKey keyFor(object id, string tenantId = "*default*", string database = "db-1")
        => new(typeof(Basket), database, tenantId, id);

    [Fact]
    public void a_take_against_an_empty_cache_misses()
    {
        theCache.TryTake(keyFor(Guid.NewGuid()), out var aggregate, out var version).ShouldBeFalse();

        aggregate.ShouldBeNull();
        version.ShouldBe(0);
    }

    [Fact]
    public void store_then_take_hands_back_the_same_instance_and_version()
    {
        var key = keyFor(Guid.NewGuid());
        var basket = new Basket();

        theCache.Store(key, basket, 12);

        theCache.TryTake(key, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(basket);
        version.ShouldBe(12);
    }

    /// <remarks>
    /// Take-on-read is a contract requirement, not an implementation detail: a store folds delta
    /// events onto the instance it is handed, so leaving the entry in place would let a second
    /// caller mutate an aggregate someone else is already using.
    /// </remarks>
    [Fact]
    public void a_take_removes_the_entry()
    {
        var key = keyFor(Guid.NewGuid());
        theCache.Store(key, new Basket(), 3);

        theCache.TryTake(key, out _, out _).ShouldBeTrue();
        theCache.TryTake(key, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void exactly_one_of_many_concurrent_takers_wins_an_entry()
    {
        var key = keyFor(Guid.NewGuid());
        theCache.Store(key, new Basket(), 7);

        var winners = 0;
        Parallel.For(0, 64, i =>
        {
            if (theCache.TryTake(key, out _, out _))
            {
                Interlocked.Increment(ref winners);
            }
        });

        winners.ShouldBe(1);
    }

    [Fact]
    public void evict_drops_the_entry()
    {
        var key = keyFor(Guid.NewGuid());
        theCache.Store(key, new Basket(), 4);

        theCache.Evict(key);

        theCache.TryTake(key, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void evicting_a_key_that_was_never_stored_is_a_no_op()
    {
        Should.NotThrow(() => theCache.Evict(keyFor(Guid.NewGuid())));
    }

    [Fact]
    public void storing_the_same_key_twice_keeps_the_later_version()
    {
        var key = keyFor(Guid.NewGuid());
        var second = new Basket();

        theCache.Store(key, new Basket(), 1);
        theCache.Store(key, second, 2);

        theCache.TryTake(key, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(second);
        version.ShouldBe(2);
    }

    [Fact]
    public void hits_and_misses_are_counted()
    {
        var key = keyFor(Guid.NewGuid());
        theCache.Store(key, new Basket(), 1);

        theCache.TryTake(key, out _, out _);
        theCache.TryTake(key, out _, out _);

        theCache.Hits.ShouldBe(1);
        theCache.Misses.ShouldBe(1);
    }

    [Fact]
    public void the_size_limit_is_honored()
    {
        var cache = new RecentlyUsedAggregateWriteCache(5);

        var keys = Enumerable.Range(0, 50).Select(_ => keyFor(Guid.NewGuid())).ToArray();
        foreach (var key in keys)
        {
            cache.Store(key, new Basket(), 1);
        }

        var surviving = keys.Count(key => cache.TryTake(key, out _, out _));

        // The eviction policy itself is not the contract -- only that the cache stays bounded, so
        // an aggregate cache on a long-lived node cannot grow without limit.
        surviving.ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public void a_size_limit_with_no_room_for_an_entry_is_rejected()
    {
        // Silently caching nothing would look like a store that ignored the opt-in.
        Should.Throw<ArgumentOutOfRangeException>(() => new RecentlyUsedAggregateWriteCache(0));
    }

    [Fact]
    public void storing_a_null_aggregate_is_rejected()
    {
        Should.Throw<ArgumentNullException>(() => theCache.Store(keyFor(Guid.NewGuid()), null!, 1));
    }
}

public class AggregateCacheKeyTests
{
    private readonly Guid theId = Guid.NewGuid();

    [Fact]
    public void the_same_four_parts_are_the_same_key()
    {
        new AggregateCacheKey(typeof(Basket), "db-1", "acme", theId)
            .ShouldBe(new AggregateCacheKey(typeof(Basket), "db-1", "acme", theId));
    }

    [Fact]
    public void a_different_tenant_is_a_different_key()
    {
        new AggregateCacheKey(typeof(Basket), "db-1", "acme", theId)
            .ShouldNotBe(new AggregateCacheKey(typeof(Basket), "db-1", "globex", theId));
    }

    /// <remarks>
    /// The database is in the key so that database-per-tenant deployments, where the same tenant id
    /// and stream id exist in more than one physical database, cannot collide.
    /// </remarks>
    [Fact]
    public void a_different_database_is_a_different_key()
    {
        new AggregateCacheKey(typeof(Basket), "db-1", "acme", theId)
            .ShouldNotBe(new AggregateCacheKey(typeof(Basket), "db-2", "acme", theId));
    }

    [Fact]
    public void a_different_document_type_is_a_different_key()
    {
        new AggregateCacheKey(typeof(Basket), "db-1", "acme", theId)
            .ShouldNotBe(new AggregateCacheKey(typeof(Order), "db-1", "acme", theId));
    }

    [Fact]
    public void a_different_identity_is_a_different_key()
    {
        new AggregateCacheKey(typeof(Basket), "db-1", "acme", theId)
            .ShouldNotBe(new AggregateCacheKey(typeof(Basket), "db-1", "acme", Guid.NewGuid()));
    }

    /// <remarks>
    /// Identity is boxed, so equality runs through <c>EqualityComparer&lt;object&gt;.Default</c>.
    /// That is structural for the three identity shapes a store supports — Guid, string, and a
    /// strong-typed wrapper over either — which is what makes a cache hit possible at all for a
    /// stream fetched twice.
    /// </remarks>
    [Fact]
    public void boxed_identities_compare_structurally()
    {
        new AggregateCacheKey(typeof(Basket), "db-1", "acme", "basket-1")
            .ShouldBe(new AggregateCacheKey(typeof(Basket), "db-1", "acme", string.Concat("basket", "-1")));

        new AggregateCacheKey(typeof(Basket), "db-1", "acme", new BasketId(theId))
            .ShouldBe(new AggregateCacheKey(typeof(Basket), "db-1", "acme", new BasketId(theId)));
    }
}

public class NulloAggregateWriteCacheTests
{
    [Fact]
    public void never_hits_no_matter_what_was_stored()
    {
        var cache = NulloAggregateWriteCache.Instance;
        var key = new AggregateCacheKey(typeof(Basket), "db-1", "acme", Guid.NewGuid());

        cache.Store(key, new Basket(), 5);

        cache.TryTake(key, out var aggregate, out var version).ShouldBeFalse();
        aggregate.ShouldBeNull();
        version.ShouldBe(0);
    }
}

public class AggregateWriteCacheOptionsTests
{
    private readonly AggregateWriteCacheOptions theOptions = new();

    [Fact]
    public void caching_is_off_for_every_type_until_something_opts_in()
    {
        theOptions.IsEnabledForAnyType.ShouldBeFalse();
        theOptions.IsEnabled(typeof(Basket)).ShouldBeFalse();
        theOptions.EnabledTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_type_nobody_enrolled_resolves_to_the_nullo_cache()
    {
        theOptions.Enable(typeof(Basket));

        theOptions.ResolveCache(typeof(Order)).ShouldBeOfType<NulloAggregateWriteCache>();
    }

    [Fact]
    public void an_enrolled_type_resolves_to_the_real_cache()
    {
        theOptions.Enable(typeof(Basket));

        theOptions.ResolveCache(typeof(Basket)).ShouldBeOfType<RecentlyUsedAggregateWriteCache>();
    }

    [Fact]
    public void every_enrolled_type_shares_one_cache_instance()
    {
        theOptions.Enable(typeof(Basket));
        theOptions.Enable(typeof(Order));

        theOptions.ResolveCache(typeof(Basket)).ShouldBeSameAs(theOptions.ResolveCache(typeof(Order)));
    }

    [Fact]
    public void a_supplied_cache_wins_over_the_default()
    {
        var custom = new RecentlyUsedAggregateWriteCache(3);
        theOptions.Cache = custom;
        theOptions.Enable(typeof(Basket));

        theOptions.ResolveCache(typeof(Basket)).ShouldBeSameAs(custom);
    }

    [Fact]
    public void enabling_a_type_twice_is_harmless()
    {
        theOptions.Enable(typeof(Basket));
        theOptions.Enable(typeof(Basket));

        theOptions.EnabledTypes.ShouldHaveSingleItem().ShouldBe(typeof(Basket));
    }
}

public class EventRegistryAggregateCachingTests
{
    private readonly EventRegistry theRegistry = new();

    [Fact]
    public void no_aggregate_type_is_cached_by_default()
    {
        theRegistry.AggregateWriteCaching.IsEnabledForAnyType.ShouldBeFalse();
    }

    [Fact]
    public void cache_aggregates_for_writing_enrolls_exactly_one_type()
    {
        theRegistry.CacheAggregatesForWriting<Basket>();

        theRegistry.AggregateWriteCaching.IsEnabled(typeof(Basket)).ShouldBeTrue();
        theRegistry.AggregateWriteCaching.IsEnabled(typeof(Order)).ShouldBeFalse();
    }

    [Fact]
    public void the_size_limit_argument_reaches_the_options()
    {
        theRegistry.CacheAggregatesForWriting<Basket>(25);

        theRegistry.AggregateWriteCaching.SizeLimit.ShouldBe(25);
    }
}

public class Basket
{
    public Guid Id { get; set; }
}

public class Order
{
    public Guid Id { get; set; }
}

public readonly record struct BasketId(Guid Value);
