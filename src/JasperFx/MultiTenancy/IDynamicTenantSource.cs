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
    /// Provision a new tenant whose connection/partition is auto-assigned by the source's configured
    /// strategy — e.g. sharded-database pooling or per-tenant event partitions — rather than a
    /// caller-supplied connection value. The default implementation throws
    /// <see cref="NotSupportedException" />; sources that auto-assign (e.g. Marten's sharded tenancy)
    /// override it. This is the uniform "add a tenant" entrypoint for provisioning models that own the
    /// physical assignment, so a tool such as CritterWatch never has to sniff the concrete tenancy type.
    /// See jasperfx#409.
    /// </summary>
    Task AddTenantAsync(string tenantId, CancellationToken token = default)
        => throw new NotSupportedException(
            $"This tenant source ({GetType().FullName}) requires a caller-supplied connection value; call AddTenantAsync(tenantId, connectionValue) instead, or use a source that supports auto-assignment.");

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
