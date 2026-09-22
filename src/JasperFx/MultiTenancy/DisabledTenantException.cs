namespace JasperFx.MultiTenancy;

/// <summary>
///     Thrown when a store refuses to open a session for a tenant it <em>does</em> know about,
///     because that tenant has been disabled. The tenant's data is untouched; disabling is
///     reversible through whatever tenancy source owns the tenant.
/// </summary>
/// <remarks>
///     <para>
///         Lifted from <c>Fisher.Storage.DisabledTenantException</c> (jasperfx#875). Marten's
///         <c>MasterTableTenancy</c> / <c>ShardedTenancy</c> and Polecat's <c>MasterTableTenancy</c>
///         report a disabled row through <see cref="UnknownTenantIdException" />, so the operator
///         who just ran CritterWatch's <c>disable_tenant</c> — or the on-call engineer after them —
///         reads "Unknown tenant id 'acme'" about a tenant that is still there and was turned off
///         on purpose.
///     </para>
///     <para>
///         It derives from <see cref="UnknownTenantIdException" /> deliberately. An existing
///         <c>catch (UnknownTenantIdException)</c> keeps catching a disabled tenant, so a store can
///         start throwing this without breaking callers, and the inheritance says the true thing:
///         a disabled tenant still refuses the session, it just refuses it for a reason the store
///         can name.
///     </para>
/// </remarks>
public class DisabledTenantException: UnknownTenantIdException
{
    public DisabledTenantException(string tenantId): this(
        $"Tenant '{tenantId}' is registered but disabled, so this store will not open a session for it. Re-enable it through the tenancy source that owns it; its data is untouched.",
        tenantId)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one — Fisher's names the
    ///     tenant's database file — and whose tests may assert on that wording.
    /// </summary>
    protected DisabledTenantException(string message, string tenantId): base(message, tenantId)
    {
    }
}
