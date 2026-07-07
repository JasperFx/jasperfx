namespace JasperFx.Events.Daemon;

/// <summary>
/// Store-agnostic source of the tenants that need rebuilding when a projection is rebuilt "everywhere".
/// A partitioned event store (Marten/Polecat, marten#4596) implements this to enumerate the tenants that
/// actually carry data for the projection — typically the distinct tenant ids in its
/// <c>(name, tenant_id)</c> progression rows, or the registered tenant partitions. jasperfx#407 Phase 2.
/// </summary>
public interface ICrossTenantRebuildSource
{
    /// <summary>
    /// The tenants to rebuild when rebuilding <paramref name="projectionName" /> across all tenants.
    /// </summary>
    Task<IReadOnlyList<string>> FindRebuildTenantsAsync(string projectionName, CancellationToken token);
}

/// <summary>
/// Store-agnostic coordinator for a cross-tenant "rebuild X everywhere" operation: a fan-out of N
/// independent per-tenant rebuilds running in parallel. Each per-tenant rebuild is scoped to its own
/// partition and <c>(name, tenant_id)</c> progression row and pauses only that tenant's shard, so other
/// tenants keep running — that isolation is enforced by the store implementation; here we only enumerate
/// targets and launch the per-tenant rebuilds through the existing
/// <see cref="IProjectionDaemon.RebuildProjectionAsync(string,string?,TimeSpan,CancellationToken)" /> overload.
/// (Combines with CritterWatch#208's per-stream to-do table carrying tenant_id; that optimization is not
/// implemented here.) jasperfx#407 Phase 2.
/// </summary>
public class CrossTenantRebuild
{
    private readonly ICrossTenantRebuildSource _source;

    public CrossTenantRebuild(ICrossTenantRebuildSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Enumerate the projection's tenants and run a per-tenant rebuild for each, in parallel. Returns the
    /// tenants that were rebuilt.
    /// </summary>
    /// <param name="daemon">The running projection daemon.</param>
    /// <param name="projectionName">Projection to rebuild for every tenant.</param>
    /// <param name="shardTimeout">Per-shard rebuild timeout.</param>
    /// <param name="token"></param>
    /// <param name="maxParallelism">Maximum simultaneous per-tenant rebuilds. Defaults to 4
    /// (epic #486 WS3 — an unbounded default let a 100-tenant store fan out 100 concurrent
    /// rebuilds against one database); pass 0 for the old unbounded behavior.</param>
    public async Task<IReadOnlyList<string>> RebuildEverywhereAsync(IProjectionDaemon daemon, string projectionName,
        TimeSpan shardTimeout, CancellationToken token, int maxParallelism = 4)
    {
        var tenants = await _source.FindRebuildTenantsAsync(projectionName, token).ConfigureAwait(false);
        if (tenants.Count == 0)
        {
            return tenants;
        }

        if (maxParallelism <= 0)
        {
            await Task.WhenAll(tenants.Select(tenantId =>
                daemon.RebuildProjectionAsync(projectionName, tenantId, shardTimeout, token))).ConfigureAwait(false);

            return tenants;
        }

        using var gate = new SemaphoreSlim(maxParallelism);
        await Task.WhenAll(tenants.Select(async tenantId =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await daemon.RebuildProjectionAsync(projectionName, tenantId, shardTimeout, token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return tenants;
    }
}
