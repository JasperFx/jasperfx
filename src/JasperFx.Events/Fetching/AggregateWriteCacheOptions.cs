namespace JasperFx.Events.Fetching;

/// <summary>
/// Opt-in configuration for caching aggregate snapshots across <c>FetchForWriting</c> calls.
/// <b>Off for every aggregate type by default.</b>
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="EventRegistry.AggregateWriteCaching" />, which every store's event
/// options derive from; enabled per aggregate type with
/// <see cref="EventRegistry.CacheAggregatesForWriting{T}" />. Opt-in per type rather than a
/// store-wide switch because the win is proportional to how often one stream is fetched for writing
/// — real on a hot aggregate under high message volume, and only overhead on an aggregate written
/// once.
/// </para>
/// <para>
/// See <see cref="IAggregateWriteCache" /> for the semantics a store owes the caller, and
/// <see href="https://github.com/JasperFx/jasperfx/issues/674" /> for the measurements.
/// </para>
/// </remarks>
public class AggregateWriteCacheOptions
{
    private readonly object _locker = new();
    private readonly HashSet<Type> _enabled = new();
    private IAggregateWriteCache? _cache;
    private int _sizeLimit = 1000;

    /// <summary>
    /// Maximum number of cached aggregates when the default
    /// <see cref="RecentlyUsedAggregateWriteCache" /> is built. Ignored when <see cref="Cache" /> is
    /// supplied, and ignored after the default cache has been built.
    /// </summary>
    public int SizeLimit
    {
        get => _sizeLimit;
        set => _sizeLimit = value;
    }

    /// <summary>
    /// Supply your own cache implementation. When left null a
    /// <see cref="RecentlyUsedAggregateWriteCache" /> honoring <see cref="SizeLimit" /> is built on
    /// first use.
    /// </summary>
    /// <remarks>
    /// A cache shared between two stores is shared honestly — keys carry the document type, the
    /// database identifier and the tenant — with one exception worth knowing: two stores mapping the
    /// same document type to different schemas within one database would collide, because a schema
    /// is not part of the key.
    /// </remarks>
    public IAggregateWriteCache? Cache
    {
        get => _cache;
        set
        {
            lock (_locker)
            {
                _cache = value;
            }
        }
    }

    /// <summary>
    /// Is any aggregate type enrolled at all? A store can use this to skip wiring up its write-back
    /// machinery entirely on the overwhelmingly common path where nobody opted in.
    /// </summary>
    public bool IsEnabledForAnyType
    {
        get
        {
            lock (_locker)
            {
                return _enabled.Count > 0;
            }
        }
    }

    /// <summary>
    /// The aggregate types currently enrolled.
    /// </summary>
    public IReadOnlyList<Type> EnabledTypes
    {
        get
        {
            lock (_locker)
            {
                return _enabled.ToArray();
            }
        }
    }

    /// <summary>
    /// Is snapshot caching enabled for this aggregate type?
    /// </summary>
    public bool IsEnabled(Type aggregateType)
    {
        lock (_locker)
        {
            return _enabled.Contains(aggregateType);
        }
    }

    /// <summary>
    /// Enable snapshot caching for an aggregate type.
    /// </summary>
    public void Enable(Type aggregateType)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);

        lock (_locker)
        {
            _enabled.Add(aggregateType);
        }
    }

    /// <summary>
    /// Resolve the cache to use, building the default
    /// <see cref="RecentlyUsedAggregateWriteCache" /> on first use.
    /// </summary>
    /// <remarks>
    /// A store should call this once when it builds its fetch plans, not per fetch. Returns
    /// <see cref="NulloAggregateWriteCache.Instance" /> for a type nobody enrolled, so the caller
    /// needs no branch of its own — every take misses and every store is discarded.
    /// </remarks>
    public IAggregateWriteCache ResolveCache(Type aggregateType)
    {
        return IsEnabled(aggregateType) ? ResolveCache() : NulloAggregateWriteCache.Instance;
    }

    /// <summary>
    /// Resolve the shared cache instance regardless of which types are enrolled, building the
    /// default <see cref="RecentlyUsedAggregateWriteCache" /> on first use.
    /// </summary>
    public IAggregateWriteCache ResolveCache()
    {
        if (_cache != null)
        {
            return _cache;
        }

        lock (_locker)
        {
            return _cache ??= new RecentlyUsedAggregateWriteCache(_sizeLimit);
        }
    }
}
