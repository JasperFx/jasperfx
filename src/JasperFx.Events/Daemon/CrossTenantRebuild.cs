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
    /// <param name="maxParallelism">Maximum simultaneous per-tenant rebuild launches. When null
    /// (the default), follows the daemon's shared per-database rebuild budget
    /// (<see cref="IProjectionDaemon.MaxConcurrentRebuildsPerDatabase" />, jasperfx#497) so this
    /// fan-out stays consistent with the cap the rebuild cells draw from; falls back to 4 when the
    /// daemon reports no budget (epic #486 WS3 — an unbounded default let a 100-tenant store fan out
    /// 100 concurrent rebuilds against one database). Pass 0 or a negative value for the old
    /// unbounded behavior. Whatever the launch width, the per-tenant replays themselves also draw
    /// from the daemon's shared budget, so cells never exceed it.</param>
    public async Task<IReadOnlyList<string>> RebuildEverywhereAsync(IProjectionDaemon daemon, string projectionName,
        TimeSpan shardTimeout, CancellationToken token, int? maxParallelism = null)
    {
        var launchWidth = ResolveLaunchWidth(maxParallelism, daemon.MaxConcurrentRebuildsPerDatabase);

        var tenants = await _source.FindRebuildTenantsAsync(projectionName, token).ConfigureAwait(false);
        if (tenants.Count == 0)
        {
            return tenants;
        }

        if (launchWidth <= 0)
        {
            await Task.WhenAll(tenants.Select(tenantId =>
                daemon.RebuildProjectionAsync(projectionName, tenantId, shardTimeout, token))).ConfigureAwait(false);

            return tenants;
        }

        using var gate = new SemaphoreSlim(launchWidth);
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

    /// <summary>
    /// jasperfx#497: resolve the effective launch width for the cross-tenant fan-out. An explicit
    /// <paramref name="maxParallelism" /> always wins (non-positive = unbounded); otherwise follow the
    /// daemon's shared per-database rebuild budget so this layer stays consistent with the cap the
    /// rebuild cells draw from; otherwise the #496 default of 4.
    /// </summary>
    internal static int ResolveLaunchWidth(int? maxParallelism, int? daemonBudget)
    {
        if (maxParallelism.HasValue) return maxParallelism.Value;
        if (daemonBudget.HasValue) return daemonBudget.Value;
        return 4;
    }
}
