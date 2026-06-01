namespace JasperFx.Events.Daemon.HighWater;

public interface IHighWaterDetector
{
    Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token);
    Task<HighWaterStatistics> Detect(CancellationToken token);
    Uri DatabaseUri { get; }

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
}
