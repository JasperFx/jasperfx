using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.MultiTenancy;

/// <summary>
/// One uniform entrypoint for managing dynamic multi-tenancy on a running Critter Stack service,
/// regardless of which tenancy model backs it (Marten master-table, sharded-database pooling, Wolverine's
/// master tenant source, or a future Polecat model). Dispatches each admin operation to every registered
/// <see cref="IDynamicTenantSource{T}" /> of <c>string</c> connection values, so a tool such as
/// CritterWatch consumes only these extensions and never sniffs the concrete tenancy type. Following the
/// store-agnostic pattern, every method is a graceful no-op when no dynamic tenant source is registered.
/// See jasperfx#409 (CritterWatch#209).
/// </summary>
public static class DynamicTenancyAdminExtensions
{
    /// <summary>
    /// All registered dynamic (string-keyed) tenant sources. Empty when the service has no dynamic
    /// tenancy model registered.
    /// </summary>
    public static IReadOnlyList<IDynamicTenantSource<string>> DynamicTenantSources(this IServiceProvider services)
        => services.GetServices<IDynamicTenantSource<string>>().ToList();

    /// <summary>
    /// Add a tenant with a caller-supplied connection value (master-table style). Dispatched to every
    /// registered dynamic tenant source.
    /// </summary>
    public static Task AddTenantAsync(this IServiceProvider services, string tenantId, string connectionValue)
        => forEachSource(services, source => source.AddTenantAsync(tenantId, connectionValue));

    /// <summary>
    /// Add a tenant whose connection/partition is auto-assigned by the source (sharded/partitioned style;
    /// no caller-supplied value). Dispatched to every registered dynamic tenant source — a source that
    /// requires a connection value will surface <see cref="NotSupportedException" /> from its default
    /// <see cref="IDynamicTenantSource{T}.AddTenantAsync(string,CancellationToken)" />.
    /// </summary>
    public static Task AddTenantAsync(this IServiceProvider services, string tenantId,
        CancellationToken token = default)
        => forEachSource(services, source => source.AddTenantAsync(tenantId, token));

    /// <summary>
    /// Disable (soft delete) a tenant across every registered dynamic tenant source.
    /// </summary>
    public static Task DisableTenantAsync(this IServiceProvider services, string tenantId)
        => forEachSource(services, source => source.DisableTenantAsync(tenantId));

    /// <summary>
    /// Re-enable a previously disabled tenant across every registered dynamic tenant source.
    /// </summary>
    public static Task EnableTenantAsync(this IServiceProvider services, string tenantId)
        => forEachSource(services, source => source.EnableTenantAsync(tenantId));

    /// <summary>
    /// Remove a tenant from the registry across every registered dynamic tenant source. Does not delete
    /// the tenant's data.
    /// </summary>
    public static Task RemoveTenantAsync(this IServiceProvider services, string tenantId)
        => forEachSource(services, source => source.RemoveTenantAsync(tenantId));

    /// <summary>
    /// The distinct set of currently disabled tenants across every registered dynamic tenant source.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AllDisabledTenantsAsync(this IServiceProvider services)
    {
        var disabled = new List<string>();
        foreach (var source in services.DynamicTenantSources())
        {
            disabled.AddRange(await source.AllDisabledAsync().ConfigureAwait(false));
        }

        return disabled.Distinct().ToList();
    }

    private static async Task forEachSource(IServiceProvider services,
        Func<IDynamicTenantSource<string>, Task> action)
    {
        foreach (var source in services.DynamicTenantSources())
        {
            await action(source).ConfigureAwait(false);
        }
    }

    // ---- IHost conveniences (the natural admin entrypoint for a monitored service) ----

    /// <summary><inheritdoc cref="DynamicTenantSources(IServiceProvider)" /></summary>
    public static IReadOnlyList<IDynamicTenantSource<string>> DynamicTenantSources(this IHost host)
        => host.Services.DynamicTenantSources();

    /// <summary><inheritdoc cref="AddTenantAsync(IServiceProvider,string,string)" /></summary>
    public static Task AddTenantAsync(this IHost host, string tenantId, string connectionValue)
        => host.Services.AddTenantAsync(tenantId, connectionValue);

    /// <summary><inheritdoc cref="AddTenantAsync(IServiceProvider,string,CancellationToken)" /></summary>
    public static Task AddTenantAsync(this IHost host, string tenantId, CancellationToken token = default)
        => host.Services.AddTenantAsync(tenantId, token);

    /// <summary><inheritdoc cref="DisableTenantAsync(IServiceProvider,string)" /></summary>
    public static Task DisableTenantAsync(this IHost host, string tenantId)
        => host.Services.DisableTenantAsync(tenantId);

    /// <summary><inheritdoc cref="EnableTenantAsync(IServiceProvider,string)" /></summary>
    public static Task EnableTenantAsync(this IHost host, string tenantId)
        => host.Services.EnableTenantAsync(tenantId);

    /// <summary><inheritdoc cref="RemoveTenantAsync(IServiceProvider,string)" /></summary>
    public static Task RemoveTenantAsync(this IHost host, string tenantId)
        => host.Services.RemoveTenantAsync(tenantId);

    /// <summary><inheritdoc cref="AllDisabledTenantsAsync(IServiceProvider)" /></summary>
    public static Task<IReadOnlyList<string>> AllDisabledTenantsAsync(this IHost host)
        => host.Services.AllDisabledTenantsAsync();
}
