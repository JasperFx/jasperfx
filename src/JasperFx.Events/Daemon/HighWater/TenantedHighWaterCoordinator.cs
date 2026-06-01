using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
/// Base-daemon mechanism that drives per-tenant high-water for a partitioned event store. It owns a
/// single <see cref="VectorizedHighWaterMonitor" /> per database (one vectorized agent, NOT one per
/// tenant), keeps the polled-tenant set in step with the shards currently assigned to this node, and
/// routes each tenant's high-water mark to that tenant's subscription agents only — so a stale/flat
/// tenant never stalls or skews another. The store supplies the real vectorized poll through
/// <see cref="IHighWaterDetector" />; this coordinator is pure, store-agnostic, and unit-testable in
/// isolation. Lives in the base daemon so Marten + Polecat inherit the behavior. jasperfx#407 Phase 2b.
/// </summary>
public class TenantedHighWaterCoordinator
{
    private readonly VectorizedHighWaterMonitor _monitor;
    private readonly ILogger _logger;

    public TenantedHighWaterCoordinator(IHighWaterDetector detector, ILogger? logger = null)
    {
        _monitor = new VectorizedHighWaterMonitor(detector, logger);
        _logger = logger ?? NullLogger.Instance;
    }

    public PolledTenantSet PolledTenants => _monitor.PolledTenants;

    /// <summary>
    /// The rebuild ceiling for a tenant — its latest observed high-water mark. Null until first polled.
    /// </summary>
    public long? CeilingFor(string tenantId) => _monitor.CeilingFor(tenantId);

    /// <summary>
    /// Reconcile the polled-tenant set with the shards currently assigned to this node: a tenant is
    /// polled exactly when at least one of its shards is running here. Store-global shards (null
    /// <see cref="ShardName.TenantId" />) contribute nothing — they stay on the global high-water agent.
    /// </summary>
    public void SyncAssignedTenants(IEnumerable<ShardName> assignedShards)
    {
        var tenants = assignedShards
            .Select(x => x.TenantId)
            .Where(tenantId => tenantId != null)
            .Distinct()
            .ToList();

        _monitor.PolledTenants.SetTenants(tenants!);
    }

    /// <summary>
    /// Poll the per-tenant high-water vector once and push each tenant's mark to that tenant's agents.
    /// Tenants are detected and routed independently; an empty polled set is a no-op. Returns the readings
    /// for observability/testing.
    /// </summary>
    public async Task<IReadOnlyList<TenantHighWaterReading>> PollAndRouteAsync(
        IReadOnlyList<ISubscriptionAgent> agents, CancellationToken token)
    {
        var readings = await _monitor.PollAsync(token).ConfigureAwait(false);

        foreach (var reading in readings)
        {
            foreach (var agent in agents)
            {
                // Route only to that tenant's shards. A tenant's stale mark never reaches another tenant.
                if (agent.Name.TenantId == reading.TenantId)
                {
                    agent.MarkHighWater(reading.Statistics.CurrentMark);
                }
            }
        }

        return readings;
    }
}
