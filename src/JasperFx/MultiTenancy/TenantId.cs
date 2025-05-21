using JasperFx.Core;

namespace JasperFx.MultiTenancy;

#region sample_TenantId

/// <summary>
/// Strong typed identifier for the tenant id within a Wolverine message handler
/// or HTTP endpoint that is using multi-tenancy
/// </summary>
/// <param name="Value">The active tenant id. Note that this can be null</param>
public record TenantId(string Value)
{
    public const string DefaultTenantId = "*DEFAULT*";

    /// <summary>
    /// Is there a non-default tenant id?
    /// </summary>
    /// <returns></returns>
    public bool IsEmpty() => Value.IsEmpty() || Value == DefaultTenantId;

    public bool IsDefault() => Value.IsDefaultTenant();
}

#endregion

/// <summary>
/// Value object to represent a connection string
/// </summary>
/// <param name="Value"></param>
public record ConnectionString(string Value);

