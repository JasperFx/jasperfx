namespace JasperFx.MultiTenancy;

/// <summary>
/// Extends ITenantedSource for dynamic tenancy models that support
/// adding and removing tenants at runtime.
/// </summary>
public interface IDynamicTenantSource<T> : ITenantedSource<T>
{
    /// <summary>
    /// Add a new tenant with the given identifier and connection value.
    /// </summary>
    Task AddTenantAsync(string tenantId, T connectionValue);

    /// <summary>
    /// Disable a tenant (soft delete). The tenant data is preserved but
    /// the tenant is no longer active. Agents are stopped, caches evicted.
    /// </summary>
    Task DisableTenantAsync(string tenantId);

    /// <summary>
    /// Remove a tenant entirely from the tenant registry (hard delete from
    /// master table). Does NOT delete the tenant's data/database.
    /// Agents are stopped, caches evicted, store disposed.
    /// </summary>
    Task RemoveTenantAsync(string tenantId);

    /// <summary>
    /// Returns all currently disabled tenants.
    /// </summary>
    Task<IReadOnlyList<string>> AllDisabledAsync();

    /// <summary>
    /// Re-enable a previously disabled tenant.
    /// </summary>
    Task EnableTenantAsync(string tenantId);
}
