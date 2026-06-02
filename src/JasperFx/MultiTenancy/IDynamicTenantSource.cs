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
    /// caller-supplied connection value. Returns the resolved assignment: the database id (sharded pool)
    /// or partition suffix (managed partitions) the tenant landed on, so the caller (e.g. CritterWatch)
    /// learns where it was placed without sniffing the concrete tenancy type. The default implementation
    /// throws <see cref="NotSupportedException" />; auto-assign sources (e.g. Marten's sharded tenancy)
    /// override it, while caller-supplies-value sources keep
    /// <see cref="AddTenantAsync(string,T)" />. See jasperfx#413 (split from #409).
    /// </summary>
    Task<string> AddTenantAsync(string tenantId, CancellationToken token = default)
        => throw new NotSupportedException(
            $"This tenant source ({GetType().FullName}) does not support auto-assignment; call AddTenantAsync(tenantId, connectionValue) with a caller-supplied connection value instead.");

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
