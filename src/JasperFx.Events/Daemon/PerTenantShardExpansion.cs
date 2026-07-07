using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// jasperfx#489: distribution-build-time expansion of store-global shard names into
/// per-tenant shard names for stores that distribute agents per tenant
/// (<see cref="IEventStore.DistributesAgentsPerTenant"/>). Shared by the Solo /
/// SingleTenant / MultiTenanted <see cref="IProjectionDistributor"/> concretes so the
/// native HotCold coordination loop starts tenant-bearing identities through
/// <c>JasperFxAsyncDaemon.StartAgentAsync</c>'s per-tenant branch (#487) instead of a
/// single store-global agent per shard.
/// </summary>
/// <remarks>
/// The tenant source is the same one <c>JasperFxAsyncDaemon.StartAllAsync</c> /
/// <c>buildPerTenantContinuousAgents</c> uses: the database's
/// <see cref="ICrossTenantRebuildSource"/> implementation (Marten: the registered
/// partitions in <c>mt_tenant_partitions</c>). A database with no tenant source, or a
/// projection whose tenant list is empty, keeps its store-global shard name — the
/// zero-tenant fallback mirrors <c>buildPerTenantContinuousAgents</c> so the shard
/// still runs (there are no events to process until a tenant exists).
///
/// Expansion is re-evaluated on every <c>BuildDistributionAsync</c> call, so tenants
/// added or removed at runtime (Marten's <c>AddMartenManagedTenantsAsync</c> /
/// <c>AddTenantToShardAsync</c>, possibly from another node) converge on the next
/// leadership polling cycle without a restart.
/// </remarks>
public static class PerTenantShardExpansion
{
    /// <summary>
    /// Expand each store-global shard name into per-tenant shard names when
    /// <paramref name="database"/> can enumerate tenants. Returns
    /// <paramref name="shards"/> untouched when the database is not an
    /// <see cref="ICrossTenantRebuildSource"/>; a shard whose tenant list is empty
    /// keeps its store-global name.
    /// </summary>
    public static async ValueTask<IReadOnlyList<ShardName>> ExpandAsync(
        IProjectionDatabase database,
        IReadOnlyList<ShardName> shards,
        CancellationToken token = default)
    {
        if (database is not ICrossTenantRebuildSource tenantSource)
        {
            return shards;
        }

        var expanded = new List<ShardName>(shards.Count);
        foreach (var shard in shards)
        {
            // Same lookup key buildPerTenantContinuousAgents uses — the projection
            // name, not the shard identity.
            var tenants = await tenantSource
                .FindRebuildTenantsAsync(shard.Name, token).ConfigureAwait(false);

            if (tenants.Count == 0)
            {
                expanded.Add(shard);
                continue;
            }

            foreach (var tenantId in tenants)
            {
                expanded.Add(shard.ForTenant(tenantId));
            }
        }

        return expanded;
    }
}
