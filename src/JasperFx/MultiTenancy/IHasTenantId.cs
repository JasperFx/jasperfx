using JasperFx.Core;

namespace JasperFx.MultiTenancy;

/// <summary>
/// Marker interface for JasperFx types that
/// are related to a tenant id
/// </summary>
public interface IHasTenantId
{
    string? TenantId { get; set; }
}

public static class HasTenantIdExtensions
{
    /// <summary>
    /// Does the subject have a non-default tenant id?
    /// </summary>
    /// <param name="subject"></param>
    /// <returns></returns>
    public static bool IsDefaultTenant(this IHasTenantId subject)
    {
        return subject.TenantId.IsDefaultTenant();
    }

    public static bool IsDefaultTenant(this string? tenantId)
    {
        return tenantId.IsEmpty() || tenantId == StorageConstants.DefaultTenantId;
    }
}