namespace JasperFx.MultiTenancy;

public enum TenantIdStyle
{
    /// <summary>
    /// Use the tenant id as is wherever it is supplied
    /// </summary>
    CaseSensitive,

    /// <summary>
    /// Quietly convert all supplied tenant identifiers to all upper case to prevent
    /// any possible issues with case sensitive tenant id mismatches
    /// </summary>
    ForceUpperCase,

    /// <summary>
    /// Quietly convert all supplied tenant identifiers to all lower case to prevent
    /// any possible issues with case sensitive tenant id mismatches
    /// </summary>
    ForceLowerCase
}

public static class TenantIdStyleExtensions
{
    public static string MaybeCorrectTenantId(this TenantIdStyle tenantIdStyle, string tenantId)
    {
        if (tenantId.IsDefaultTenant()) return StorageConstants.DefaultTenantId;

        switch (tenantIdStyle)
        {
            case TenantIdStyle.CaseSensitive:
                return tenantId;
            case TenantIdStyle.ForceLowerCase:
                return tenantId.ToLowerInvariant();
            default:
                return tenantId.ToUpperInvariant();
        }
    }
}
