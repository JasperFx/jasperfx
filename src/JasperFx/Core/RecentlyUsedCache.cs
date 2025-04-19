using System.Diagnostics.CodeAnalysis;
using ImTools;

namespace JasperFx.Core;

public interface IAggregateCache<TKey, TItem> where TKey: notnull where TItem: notnull
{
    bool TryFind(TKey key, [NotNullWhen(true)]out TItem? item);
    void Store(TKey key, TItem item);
    void CompactIfNecessary();
    void TryRemove(TKey key);
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
}

public class RecentlyUsedCache<TKey, TItem>: IAggregateCache<TKey, TItem> where TKey : notnull where TItem : notnull
{
    private ImHashMap<TKey, TItem> _items = ImHashMap<TKey, TItem>.Empty;
    private ImHashMap<TKey, DateTimeOffset> _times = ImHashMap<TKey, DateTimeOffset>.Empty;

    public int Limit = 100;

    public int Count => _items.Count();

    public bool TryFind(TKey key, [NotNullWhen(true)]out TItem? item)
    {
        if (_items.TryFind(key, out item))
        {
            _times = _times.AddOrUpdate(key, DateTimeOffset.UtcNow);
            return true;
        }

        item = default;
        return false;
    }

    public void Store(TKey key, TItem item)
    {
        _items = _items.AddOrUpdate(key, item);
        _times = _times.AddOrUpdate(key, DateTimeOffset.UtcNow);
    }

    public void CompactIfNecessary()
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
            _items = _items.Remove(key);
            _items = _items.Remove(key);
        }
    }

    public void TryRemove(TKey key)
    {
        _items = _items.Remove(key);
    }
}
