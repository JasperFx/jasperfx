using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Fetching;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// An <see cref="IAggregateWriteCache" /> that records what the store asked of it, for
/// <see cref="AggregateWriteCacheCompliance{TFixture,TOperations,TQuerySession}" />.
/// </summary>
/// <remarks>
/// <para>
/// Two things it makes possible that no purely behavioral suite could do.
/// </para>
/// <para>
/// First, it proves the cache was <em>consulted</em>. Every correctness fact about caching is
/// vacuously true of a store that ignored the opt-in entirely, because an uncached fetch is correct
/// by construction. <see cref="Hits" /> is what tells the difference. Same reasoning as the gzipped
/// serializer in <see cref="BinaryEventSerializationCompliance{TFixture}" />.
/// </para>
/// <para>
/// Second, it hands the suite a real <see cref="AggregateCacheKey" /> to poison. The key carries the
/// store's own database identifier and tenant resolution, so a suite cannot construct one — but it
/// can capture the one the store stored and then <see cref="Seed" /> that key with a deliberately
/// wrong baseline.
/// </para>
/// <para>
/// Take-on-read and the size bound are delegated to the real
/// <see cref="RecentlyUsedAggregateWriteCache" /> rather than reimplemented, so a store is held to
/// the shipped implementation's semantics and not to a test double's.
/// </para>
/// </remarks>
public class RecordingAggregateWriteCache: IAggregateWriteCache
{
    private readonly object _locker = new();
    private readonly List<AggregateCacheKey> _stored = new();
    private readonly List<AggregateCacheKey> _taken = new();
    private RecentlyUsedAggregateWriteCache _inner = new();
    private long _hits;
    private long _misses;

    /// <summary>
    /// Successful takes — the count that separates a store which honored the opt-in from one that
    /// silently did not.
    /// </summary>
    public long Hits
    {
        get { lock (_locker) { return _hits; } }
    }

    public long Misses
    {
        get { lock (_locker) { return _misses; } }
    }

    /// <summary>
    /// Every key the store has written, in order. Duplicates are kept — a store writing the same
    /// key twice in one round is worth being able to see.
    /// </summary>
    public IReadOnlyList<AggregateCacheKey> StoredKeys
    {
        get { lock (_locker) { return _stored.ToArray(); } }
    }

    /// <summary>
    /// Every key the store has successfully taken, in order.
    /// </summary>
    public IReadOnlyList<AggregateCacheKey> TakenKeys
    {
        get { lock (_locker) { return _taken.ToArray(); } }
    }

    /// <summary>
    /// The most recent key stored for a document type, or null when the store has never cached one.
    /// </summary>
    public AggregateCacheKey? LastKeyFor(Type documentType)
    {
        lock (_locker)
        {
            for (var i = _stored.Count - 1; i >= 0; i--)
            {
                if (_stored[i].DocumentType == documentType) return _stored[i];
            }

            return null;
        }
    }

    public bool WasStoredFor(Type documentType) => LastKeyFor(documentType).HasValue;

    /// <summary>
    /// Plant an entry directly, bypassing the counters. The suite uses this to poison a key the
    /// store previously stored — a stale baseline, or one ahead of the stream.
    /// </summary>
    public void Seed(AggregateCacheKey key, object aggregate, long version)
    {
        _inner.Store(key, aggregate, version);
    }

    /// <summary>
    /// Drop every entry and reset the counters. Called between arrange and act so a fact asserts on
    /// the round it is actually testing.
    /// </summary>
    public void Reset()
    {
        lock (_locker)
        {
            _inner = new RecentlyUsedAggregateWriteCache();
            _stored.Clear();
            _taken.Clear();
            _hits = 0;
            _misses = 0;
        }
    }

    public bool TryTake(AggregateCacheKey key, [NotNullWhen(true)] out object? aggregate, out long version)
    {
        var taken = _inner.TryTake(key, out aggregate, out version);

        lock (_locker)
        {
            if (taken)
            {
                _hits++;
                _taken.Add(key);
            }
            else
            {
                _misses++;
            }
        }

        return taken;
    }

    public void Store(AggregateCacheKey key, object aggregate, long version)
    {
        _inner.Store(key, aggregate, version);

        lock (_locker)
        {
            _stored.Add(key);
        }
    }

    public void Evict(AggregateCacheKey key)
    {
        _inner.Evict(key);
    }
}
