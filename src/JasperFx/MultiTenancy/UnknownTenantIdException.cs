namespace JasperFx.MultiTenancy;

public class UnknownTenantIdException: Exception
{
    // The cast disambiguates against the protected (message, tenantId) overload below, which a
    // bare null would match just as well from inside this class.
    public UnknownTenantIdException(string tenantId): this(tenantId, (IReadOnlyCollection<string>?)null)
    {
    }

    /// <summary>
    ///     For tenancy sources that hold the full tenant list in memory — the static and
    ///     master-table sources — so the message can say what the store <em>does</em> know. A
    ///     sharded or dynamic source that would have to go to the database to answer passes null.
    /// </summary>
    public UnknownTenantIdException(string tenantId, IReadOnlyCollection<string>? knownTenantIds)
        : this(toMessage(tenantId, knownTenantIds), tenantId)
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

    private static string toMessage(string tenantId, IReadOnlyCollection<string>? knownTenantIds)
    {
        var known = knownTenantIds is { Count: > 0 }
            ? $" It knows: {string.Join(", ", knownTenantIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}."
            : string.Empty;

        // jasperfx#874: the fact alone sends people looking for a tenant that was never registered,
        // or that was registered under a different casing, with nothing to say which. The casing
        // clause matters because TenantIdStyle is a per-component setting; the disabled clause
        // because Marten and Polecat report a disabled tenant through this same type -- until they
        // adopt DisabledTenantException (jasperfx#875), which subclasses this through the protected
        // constructor above.
        return
            $"Unknown tenant id '{tenantId}'.{known} Register the tenant with the tenancy source this store uses, check the id's casing against the store's TenantIdStyle, and note that a disabled tenant is reported the same way on stores that do not distinguish it.";
    }
}
