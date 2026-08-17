using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events.Fetching;

/// <summary>
/// Identifies a single cached aggregate snapshot.
/// </summary>
/// <param name="DocumentType">The aggregate document type.</param>
/// <param name="DatabaseIdentifier">
/// Identifier of the physical database the aggregate was read from.
/// </param>
/// <param name="TenantId">
/// The owning tenant, or <see cref="GlobalTenant" /> for aggregates registered as global.
/// </param>
/// <param name="Id">The stream / aggregate identity.</param>
/// <remarks>
/// <para>
/// The database identifier is part of the key so that database-per-tenant deployments — where the
/// very same tenant id and stream id exist in more than one physical database — cannot collide.
/// "Global" aggregates are keyed per database for the same reason: they have one instance per
/// database, not one per process.
/// </para>
/// <para>
/// <paramref name="Id" /> is typed as <see cref="object" /> because stream identity is
/// <see cref="Guid" />, <see cref="string" /> or a strong-typed wrapper depending on the store's
/// configuration. Equality therefore runs through
/// <see cref="EqualityComparer{T}.Default" />, which is structural for all three; a boxed identity
/// that did not implement value equality would simply never produce a hit, costing a load rather
/// than returning a wrong aggregate.
/// </para>
/// </remarks>
public readonly record struct AggregateCacheKey(
    Type DocumentType,
    string DatabaseIdentifier,
    string TenantId,
    object Id)
{
    /// <summary>
    /// Stand-in tenant for aggregates registered as global, which are not partitioned by tenant.
    /// </summary>
    public const string GlobalTenant = "*global*";
}

/// <summary>
/// A node-local, second-level cache of aggregate snapshots that a store's <c>FetchForWriting</c>
/// can use as a <em>baseline</em> instead of loading the stored snapshot from the database.
/// Effectively an identity map for aggregates with a lifetime longer than a session.
/// </summary>
/// <remarks>
/// <para>
/// See <see href="https://github.com/JasperFx/jasperfx/issues/674" />. The shape originated in
/// Marten and is declared here so Marten, Polecat and Fisher share one contract and one default
/// implementation, rather than a consumer targeting all three reimplementing a cache per flavour.
/// </para>
/// <para>
/// <b>The load-bearing property is that an implementation never has to be correct, only
/// <em>usually</em> correct.</b> A store may use the cached instance as a starting point, but it
/// still reads the stream version and every event after the cached version from the database on
/// every call, and the optimistic concurrency assertion on append is untouched. A stale entry
/// therefore costs a larger delta query — never a wrong aggregate, and never a suppressed
/// concurrency failure. That is what makes a node-local, incoherent cache the right shape, and what
/// rules out any coherence protocol between nodes.
/// </para>
/// <para>
/// What this removes is the snapshot <em>load</em>, and nothing else. Stated as the measurement axis
/// that matters: it reduces reads per second, where narrowing a wide document reduces bytes per
/// read. The two compose; neither replaces the other.
/// </para>
/// <para>
/// ⛔ <b>A "trusted" variant that additionally skipped the version read is explicitly retired and
/// must not be reintroduced.</b> It was gated in advance on a measurement and failed it — 0.19 ms
/// against a 13.2 ms round, about 1.4% — so it trades the concurrency guarantee for nothing. An
/// implementation of this interface that lets a caller skip the version read is not implementing
/// this interface.
/// </para>
/// <para>
/// Implementations must be safe for concurrent use. Beyond that they are free to evict whenever
/// they like: dropping an entry is always sound.
/// </para>
/// </remarks>
public interface IAggregateWriteCache
{
    /// <summary>
    /// Claim the cached snapshot for a key, <b>removing it from the cache in the same atomic
    /// step</b>.
    /// </summary>
    /// <remarks>
    /// Take-on-read rather than read-through, and this is a requirement rather than an
    /// implementation detail. Aggregates are commonly mutable and a store folds delta events onto
    /// the instance it is handed, so two callers holding the same instance would corrupt each
    /// other's result. Exactly one caller may ever win a given entry; concurrent callers miss and
    /// take the normal uncached path, which is always correct.
    /// </remarks>
    /// <param name="key">The aggregate being fetched.</param>
    /// <param name="aggregate">The claimed snapshot, when this returns <see langword="true" />.</param>
    /// <param name="version">
    /// The stream version the claimed snapshot is known to be at, when this returns
    /// <see langword="true" />; otherwise zero.
    /// </param>
    /// <returns><see langword="true" /> when a snapshot was claimed.</returns>
    bool TryTake(AggregateCacheKey key, [NotNullWhen(true)] out object? aggregate, out long version);

    /// <summary>
    /// Store a snapshot known to be at <paramref name="version" /> of its stream.
    /// </summary>
    /// <remarks>
    /// The version has to be the one the events were committed at, and a store should take it from
    /// the committed <see cref="StreamAction" /> rather than off the document — an aggregate is free
    /// to carry a <c>Version</c> property meaning something else entirely.
    /// </remarks>
    void Store(AggregateCacheKey key, object aggregate, long version);

    /// <summary>
    /// Drop any entry for this key. Always safe: the next fetch simply reloads.
    /// </summary>
    void Evict(AggregateCacheKey key);
}

/// <summary>
/// The no-op <see cref="IAggregateWriteCache" />. Every take misses and nothing is ever stored.
/// </summary>
/// <remarks>
/// The default for every aggregate type, and the reason a store's fetch path needs no
/// <c>if (caching enabled)</c> branch at all.
/// </remarks>
public sealed class NulloAggregateWriteCache: IAggregateWriteCache
{
    public static readonly NulloAggregateWriteCache Instance = new();

    public bool TryTake(AggregateCacheKey key, [NotNullWhen(true)] out object? aggregate, out long version)
    {
        aggregate = default;
        version = 0;
        return false;
    }

    public void Store(AggregateCacheKey key, object aggregate, long version)
    {
        // Nothing. Deliberately not even recording the key -- the point of this type is that a
        // store on the default path allocates and retains nothing.
    }

    public void Evict(AggregateCacheKey key)
    {
        // Nothing to drop.
    }
}
