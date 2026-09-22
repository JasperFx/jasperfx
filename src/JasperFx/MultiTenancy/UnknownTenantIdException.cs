namespace JasperFx.MultiTenancy;

public class UnknownTenantIdException: Exception
{
    public UnknownTenantIdException(string tenantId): this($"Unknown tenant id '{tenantId}'", tenantId)
    {
    }

    /// <summary>
    ///     For subclasses that report a narrower condition than "unknown" and therefore need their
    ///     own message — <see cref="DisabledTenantException" /> is the one in JasperFx — and for
    ///     store subclasses whose wording diverged.
    /// </summary>
    protected UnknownTenantIdException(string message, string tenantId): base(message)
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
