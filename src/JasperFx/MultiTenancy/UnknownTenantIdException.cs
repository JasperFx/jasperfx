namespace JasperFx.MultiTenancy;

public class UnknownTenantIdException: Exception
{
    public UnknownTenantIdException(string tenantId): base($"Unknown tenant id '{tenantId}'")
    {
        TenantId = tenantId;
    }

    /// <summary>
    ///     The tenant id that could not be resolved. Exposed so consumers can
    ///     <c>catch (UnknownTenantIdException ex) { ... ex.TenantId ... }</c>
    ///     without parsing the message string. Added in 2.0.0-alpha.7 to close
    ///     the diagnostics regression that the Polecat dedup audit (slice #224)
    ///     surfaced — Polecat's now-removed local <c>UnknownTenantException</c>
    ///     carried this getter.
    /// </summary>
    public string TenantId { get; }
}
