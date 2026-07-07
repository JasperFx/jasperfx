using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
/// Store-agnostic coordination for the "one vectorized high-water agent per database" topology. It owns
/// the <see cref="PolledTenantSet" /> (the tenants currently assigned to this node), drives a single
/// vectorized detection per poll via <see cref="IHighWaterDetector.DetectForTenantsAsync" />, and
/// interprets each tenant's high-water status <em>independently</em> against that tenant's own previous
/// reading — so one stale or flat tenant can never stall, skip, or skew another. It also answers the
/// per-tenant rebuild-ceiling lookup. The actual SQL lives in the store's <see cref="IHighWaterDetector" />
/// implementation (Marten/Polecat, marten#4596); this type is pure coordination and is fully testable in
/// isolation. jasperfx#407 Phase 2 (CritterWatch#209).
/// </summary>
public class VectorizedHighWaterMonitor
{
    private readonly IHighWaterDetector _detector;
    private readonly ILogger _logger;

    // The last-known reading per tenant. Each tenant's gap detection reads ONLY its own entry, which is
    // what keeps tenants independent of one another.
    // jasperfx#497: guarded by _lock — parallel per-tenant rebuild cells now drive concurrent polls
    // (on top of the pre-existing OnNext-driven and timer-driven poll paths), and an unguarded
    // Dictionary corrupts under concurrent writes. The detector round-trip stays outside the lock;
    // only the interpretation/bookkeeping section is serialized.
    private readonly Dictionary<string, HighWaterStatistics> _current = new();
    private readonly object _lock = new();

    public VectorizedHighWaterMonitor(IHighWaterDetector detector, ILogger? logger = null)
    {
        _detector = detector;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The tenants this monitor is currently polling. The daemon mutates this as shard assignments change.
    /// </summary>
    public PolledTenantSet PolledTenants { get; } = new();

    public Uri DatabaseUri => _detector.DatabaseUri;

    /// <summary>
    /// Poll the high-water mark for every currently-assigned tenant in a single vectorized round-trip and
    /// return one reading per tenant with an independently-interpreted status. Tenants not assigned to this
    /// node are never polled.
    /// </summary>
    public Task<IReadOnlyList<TenantHighWaterReading>> PollAsync(CancellationToken token)
        => pollAsync((tenants, t) => _detector.DetectForTenantsAsync(tenants, t), token);

    /// <summary>
    /// Safe-zone variant of <see cref="PollAsync" /> used when a tenant has gone stale and the agent needs
    /// to skip a gap in that tenant's sequence. Still independent per tenant.
    /// </summary>
    public Task<IReadOnlyList<TenantHighWaterReading>> PollSafeZoneAsync(CancellationToken token)
        => pollAsync((tenants, t) => _detector.DetectInSafeZoneForTenantsAsync(tenants, t), token);

    private async Task<IReadOnlyList<TenantHighWaterReading>> pollAsync(
        Func<IReadOnlyCollection<string>, CancellationToken, Task<HighWaterVector>> detect, CancellationToken token)
    {
        var tenants = PolledTenants.Snapshot();
        if (tenants.Count == 0)
        {
            return [];
        }

        var vector = await detect(tenants, token).ConfigureAwait(false);

        var readings = new List<TenantHighWaterReading>(tenants.Count);
        lock (_lock)
        {
            foreach (var tenantId in tenants)
            {
                if (!vector.TryGetStatistics(tenantId, out var statistics))
                {
                    // The detector returned nothing for an assigned tenant (e.g. it has no events yet). Skip
                    // it for this round without disturbing any other tenant's state.
                    continue;
                }

                // Independent gap detection: interpret against THIS tenant's previous reading only. On the
                // first sighting we compare the reading against itself, which yields CaughtUp/Changed but
                // never a spurious Stale driven by another tenant.
                var previous = _current.TryGetValue(tenantId, out var prior) ? prior : statistics;
                var status = statistics.InterpretStatus(previous);

                // Advance the stored mark monotonically; a stale tenant keeps its last good mark.
                if (!_current.TryGetValue(tenantId, out var existing) || statistics.CurrentMark >= existing.CurrentMark)
                {
                    _current[tenantId] = statistics;
                }

                readings.Add(new TenantHighWaterReading(tenantId, statistics, status));
            }
        }

        return readings;
    }

    /// <summary>
    /// The rebuild ceiling for a single tenant — the latest high-water mark this monitor has observed for
    /// that tenant. A per-tenant rebuild scans only that tenant's partition up to this ceiling. Returns
    /// null when the tenant has not yet been polled.
    /// </summary>
    public long? CeilingFor(string tenantId)
    {
        lock (_lock)
        {
            return _current.TryGetValue(tenantId, out var statistics) ? statistics.CurrentMark : null;
        }
    }

    /// <summary>
    /// The most recent reading observed for a tenant, if any.
    /// </summary>
    public bool TryGetCurrent(string tenantId, out HighWaterStatistics statistics)
    {
        lock (_lock)
        {
            return _current.TryGetValue(tenantId, out statistics!);
        }
    }
}
