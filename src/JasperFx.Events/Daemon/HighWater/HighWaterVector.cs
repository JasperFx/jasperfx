namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
/// The result of a single vectorized high-water detection round-trip: the high-water
/// <see cref="HighWaterStatistics" /> for each polled tenant, keyed by tenant id. A store that does not
/// partition events per tenant produces a one-entry vector for the store-global mark (tenant id null) —
/// that is today's single-mark behavior. jasperfx#407 Phase 2 (CritterWatch#209).
/// </summary>
public class HighWaterVector
{
    private readonly Dictionary<string, HighWaterStatistics> _byTenant = new();

    public HighWaterVector(IEnumerable<HighWaterStatistics> statistics)
    {
        foreach (var stat in statistics)
        {
            if (stat.TenantId == null)
            {
                Global = stat;
            }
            else
            {
                _byTenant[stat.TenantId] = stat;
            }
        }
    }

    /// <summary>
    /// The store-global high-water reading (tenant id null), if one was produced. This is the only
    /// reading a non-partitioned store ever emits.
    /// </summary>
    public HighWaterStatistics? Global { get; }

    /// <summary>
    /// Every per-tenant reading in this vector (excludes the store-global entry).
    /// </summary>
    public IReadOnlyCollection<HighWaterStatistics> TenantStatistics => _byTenant.Values;

    public int TenantCount => _byTenant.Count;

    public bool TryGetStatistics(string tenantId, out HighWaterStatistics statistics)
        => _byTenant.TryGetValue(tenantId, out statistics!);

    /// <summary>
    /// The rebuild ceiling for a single tenant: the current high-water mark for that tenant's partition.
    /// A null tenant resolves to the store-global mark. Returns null when the tenant is not in this vector.
    /// </summary>
    public long? CeilingFor(string? tenantId)
    {
        if (tenantId == null)
        {
            return Global?.CurrentMark;
        }

        return _byTenant.TryGetValue(tenantId, out var stat) ? stat.CurrentMark : null;
    }

    /// <summary>
    /// Build a one-entry vector for the store-global mark. Used by the default (non-partitioned)
    /// <see cref="IHighWaterDetector" /> path so existing single-mark detectors keep working unchanged.
    /// </summary>
    public static HighWaterVector ForGlobal(HighWaterStatistics global)
    {
        // Force the global slot regardless of whatever TenantId the statistics carried.
        global.TenantId = null;
        return new HighWaterVector([global]);
    }
}

/// <summary>
/// A single tenant's reading within a vectorized poll: its high-water statistics plus the
/// independently-interpreted status (caught up / changed / stale) for that tenant alone.
/// </summary>
public record TenantHighWaterReading(string TenantId, HighWaterStatistics Statistics, HighWaterStatus Status);
