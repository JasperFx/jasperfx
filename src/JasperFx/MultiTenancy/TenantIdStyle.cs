namespace JasperFx.MultiTenancy;

/// <summary>
///     How a component normalizes tenant ids as they arrive, so that <c>Acme</c> and <c>acme</c>
///     either are or are not the same tenant — deliberately, rather than by accident.
/// </summary>
/// <remarks>
///     <para>
///         Not every Critter Stack component honours this setting, so the answer to "is <c>Acme</c>
///         the same tenant as <c>acme</c>" is currently per-component. Marten
///         (<c>StoreOptions.TenantIdStyle</c>) and Wolverine (<c>opts.Durability.TenantIdStyle</c>)
///         apply it, both defaulting to <see cref="CaseSensitive" />. Polecat and Fisher do not have
///         the setting: their database-per-tenant lookups are case-insensitive while their conjoined
///         <c>tenant_id</c> comparisons are exact, so a mixed-case id finds the right database and
///         then writes rows the other spelling cannot see.
///     </para>
///     <para>
///         Two consequences worth knowing before you set this. A Wolverine host on
///         <see cref="ForceLowerCase" /> in front of a Marten store left on <see cref="CaseSensitive" />
///         throws <see cref="UnknownTenantIdException" /> for every mixed-case tenant — set the same
///         value on both, or neither. And on Polecat and Fisher, normalizing at the edge of your own
///         system is the only protection there is. See docs/configuration/tenant-id-case.md
///         (jasperfx#876).
///     </para>
/// </remarks>
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
    /// <summary>
    ///     Normalize an incoming tenant id according to the configured style. Call this at the
    ///     boundary where a tenant id arrives — before anything stores it or looks anything up with
    ///     it — which is what Marten does on every session and Wolverine does on the envelope. The
    ///     default tenant id passes through untouched whatever the style.
    /// </summary>
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
