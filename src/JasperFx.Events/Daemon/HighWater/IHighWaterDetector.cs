namespace JasperFx.Events.Daemon.HighWater;

public interface IHighWaterDetector
{
    Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token);
    Task<HighWaterStatistics> Detect(CancellationToken token);
    Uri DatabaseUri { get; }

    /// <summary>
    /// Does the backing event store partition events per tenant, such that this detector can emit a
    /// meaningful per-tenant high-water vector via <see cref="DetectForTenantsAsync" />? When false (the
    /// default) the daemon stays on the single store-global high-water mark — today's behavior, byte for
    /// byte. A partitioned store (Marten/Polecat with per-tenant sequences) overrides this to true to opt
    /// the running daemon into vectorized per-tenant high-water + per-tenant rebuilds. jasperfx#407 Phase 2b.
    /// </summary>
    bool SupportsTenantPartitioning => false;

    /// <summary>
    /// Detect the high-water statistics for a set of tenants in a single round-trip (the "vectorized"
    /// high-water contract). One detector per database emits a per-tenant high-water vector rather than
    /// the daemon running one detector per tenant. The default implementation has no tenant dimension:
    /// it runs the store-global <see cref="Detect" /> and returns a one-entry vector, so existing
    /// single-mark detectors keep compiling and behaving exactly as before. Stores that partition events
    /// per tenant override this to poll only the supplied tenants. jasperfx#407 Phase 2.
    /// </summary>
    /// <param name="tenantIds">The tenants currently assigned to this node's daemon. Empty means store-global.</param>
    /// <param name="token"></param>
    async Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken token)
    {
        var global = await Detect(token).ConfigureAwait(false);
        return HighWaterVector.ForGlobal(global);
    }

    /// <summary>
    /// Safe-zone variant of <see cref="DetectForTenantsAsync" /> used when skipping stale gaps. The
    /// default implementation delegates to the store-global <see cref="DetectInSafeZone" />. jasperfx#407 Phase 2.
    /// </summary>
    async Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
        CancellationToken token)
    {
        var global = await DetectInSafeZone(token).ConfigureAwait(false);
        return HighWaterVector.ForGlobal(global);
    }

    /// <summary>
    /// Persist a per-tenant high-water mark so each tenant's progress is durable across daemon restarts
    /// (marten#4717). Under per-tenant event partitioning each tenant's seq_id comes from its own
    /// sequence, so a single store-global high-water row cannot represent multiple tenants. The default
    /// implementation is a no-op, so non-partitioned detectors keep compiling and behaving unchanged.
    /// Partitioned stores override this to write a per-tenant high-water row keyed on the tenant.
    /// </summary>
    Task MarkHighWaterForTenantAsync(string tenantId, long sequence, CancellationToken token) => Task.CompletedTask;
}
