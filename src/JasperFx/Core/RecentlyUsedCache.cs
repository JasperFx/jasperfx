using System.Diagnostics.CodeAnalysis;
using ImTools;

namespace JasperFx.Core;

public interface IAggregateCache<TKey, TItem> where TKey: notnull where TItem: notnull
{
    bool TryFind(TKey key, [NotNullWhen(true)]out TItem? item);
    void Store(TKey key, TItem item);
    void CompactIfNecessary();
    void TryRemove(TKey key);

    bool Contains(TKey key);
}

public class DictionaryAggregateCache<TKey, TItem> : IAggregateCache<TKey, TItem>
{
    private readonly IReadOnlyDictionary<TKey, TItem> _inner;

    public DictionaryAggregateCache(IReadOnlyDictionary<TKey, TItem> inner)
    {
        _inner = inner;
    }

    public bool TryFind(TKey key, [NotNullWhen(true)] out TItem? item)
    {
        return _inner.TryGetValue(key, out item);
    }

    public void Store(TKey key, TItem item)
    {
        // nothing
    }

    public void CompactIfNecessary()
    {
        // nothing
    }

    public void TryRemove(TKey key)
    {
        // nothing
    }

    public bool Contains(TKey key)
    {
        return _inner.ContainsKey(key);
    }
}

public class NulloAggregateCache<TKey, TItem> : IAggregateCache<TKey, TItem> where TKey : notnull where TItem : notnull
{
    public bool TryFind(TKey key, out TItem item)
    {
        item = default!;
        return false;
    }

    public void Store(TKey key, TItem item)
    {
        // nothing
    }

    public void CompactIfNecessary()
    {
        // nothing
    }

    public void TryRemove(TKey key)
    {
        // nothing
    }

    public bool Contains(TKey key) => false;
}

public class RecentlyUsedCache<TKey, TItem>: IAggregateCache<TKey, TItem> where TKey : notnull where TItem : notnull
{
    // #226: serialise field updates so concurrent Store / TryFind /
    // CompactIfNecessary / TryRemove calls don't lose entries via the
    // lost-update race on `_items = _items.AddOrUpdate(...)`. The cache
    // is per-tenant + per-projection so lock contention is bounded;
    // ImHashMap's reads are already thread-safe once the field-level
    // race is closed.
    private readonly object _lock = new();
    private ImHashMap<TKey, TItem> _items = ImHashMap<TKey, TItem>.Empty;
    private ImHashMap<TKey, DateTimeOffset> _times = ImHashMap<TKey, DateTimeOffset>.Empty;

    public int Limit = 100;

    public int Count => _items.Count();

    public bool Contains(TKey key) => _items.Contains(key);

    public bool TryFind(TKey key, [NotNullWhen(true)]out TItem? item)
    {
        // Single lock around read + LRU touch. A lock-free read of
        // `_items` followed by a locked `_times` update has a TOCTOU
        // hole: an interleaved Compact can remove the key between the
        // two operations, leaving a ghost entry in `_times` that doesn't
        // correspond to anything in `_items`. CompactIfNecessary picks
        // ghosts off `_times.Enumerate()` and the `_items.Remove(key)`
        // for those becomes a no-op, so `_items.Count` stops dropping
        // to `Limit`.
        lock (_lock)
        {
            if (_items.TryFind(key, out item))
            {
                _times = _times.AddOrUpdate(key, DateTimeOffset.UtcNow);
                return true;
            }
        }

        item = default;
        return false;
    }

    public void Store(TKey key, TItem item)
    {
        lock (_lock)
        {
            _items = _items.AddOrUpdate(key, item);
            _times = _times.AddOrUpdate(key, DateTimeOffset.UtcNow);
        }
    }

    public void CompactIfNecessary()
    {
        lock (_lock)
        {
            var extraCount = _items.Count() - Limit;
            if (extraCount <= 0) return;

            var toRemove = _times
                .Enumerate()
                .OrderBy(x => x.Value)
                .Select(x => x.Key)
                .Take(extraCount)
                .ToArray();

            foreach (var key in toRemove)
            {
                // Drop from BOTH maps. Earlier code redundantly removed
                // from `_items` twice and never touched `_times`, leaving
                // it to grow unboundedly across the cache's lifetime.
                _items = _items.Remove(key);
                _times = _times.Remove(key);
            }
        }
    }

    public void TryRemove(TKey key)
    {
        lock (_lock)
        {
            _items = _items.Remove(key);
            _times = _times.Remove(key);
        }
    }
}
