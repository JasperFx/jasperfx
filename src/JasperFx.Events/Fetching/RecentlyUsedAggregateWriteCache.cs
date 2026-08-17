using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;

namespace JasperFx.Events.Fetching;

/// <summary>
/// The default <see cref="IAggregateWriteCache" />: a bounded, least-recently-used, node-local cache
/// built on JasperFx's own <see cref="RecentlyUsedCache{TKey,TItem}" />.
/// </summary>
/// <remarks>
/// <para>
/// Node-local by design rather than by omission. A cached snapshot is only ever a baseline, so
/// coherence between nodes buys nothing that the delta read does not already provide — and an
/// <c>IDistributedCache</c> would reintroduce exactly the round trip this exists to remove.
/// </para>
/// <para>
/// Backed by <see cref="RecentlyUsedCache{TKey,TItem}" /> rather than
/// <c>Microsoft.Extensions.Caching.Memory</c> deliberately: the prototype this was promoted from
/// took <c>IMemoryCache</c> as a new hard dependency on core Marten and flagged that as a decision
/// to make before graduation. Taking it here would push it onto every store and every consumer of
/// <c>JasperFx.Events</c> instead of one of them, for an LRU with a size cap that JasperFx already
/// has. A deployment that wants its entries in a shared <c>IMemoryCache</c> writes a small adapter
/// implementing the three-member interface.
/// </para>
/// </remarks>
public sealed class RecentlyUsedAggregateWriteCache: IAggregateWriteCache
{
    private readonly RecentlyUsedCache<AggregateCacheKey, Entry> _cache = new();
    private long _hits;
    private long _misses;

    /// <param name="sizeLimit">Maximum number of cached snapshots.</param>
    public RecentlyUsedAggregateWriteCache(int sizeLimit = 1000)
    {
        if (sizeLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeLimit),
                "An aggregate write cache with no room for an entry would miss on every fetch. Use NulloAggregateWriteCache to turn caching off.");
        }

        _cache.Limit = sizeLimit;
    }

    /// <summary>
    /// Number of successful takes. Diagnostics only — nothing should branch on it.
    /// </summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>
    /// Number of failed takes. Diagnostics only.
    /// </summary>
    public long Misses => Interlocked.Read(ref _misses);

    public bool TryTake(AggregateCacheKey key, [NotNullWhen(true)] out object? aggregate, out long version)
    {
        // TryFind and TryRemove are individually thread safe but not atomic together, so the claim
        // is made on the entry itself: two callers can both find it, and exactly one can transition
        // it from unclaimed to claimed. The loser reports a miss and takes the uncached path.
        if (_cache.TryFind(key, out var entry) && entry.TryClaim())
        {
            _cache.TryRemove(key);

            aggregate = entry.Aggregate;
            version = entry.Version;
            Interlocked.Increment(ref _hits);
            return true;
        }

        aggregate = default;
        version = 0;
        Interlocked.Increment(ref _misses);
        return false;
    }

    public void Store(AggregateCacheKey key, object aggregate, long version)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        _cache.Store(key, new Entry(aggregate, version));
        _cache.CompactIfNecessary();
    }

    public void Evict(AggregateCacheKey key)
    {
        _cache.TryRemove(key);
    }

    private sealed class Entry
    {
        private int _claimed;

        public Entry(object aggregate, long version)
        {
            Aggregate = aggregate;
            Version = version;
        }

        public object Aggregate { get; }
        public long Version { get; }

        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }
}
