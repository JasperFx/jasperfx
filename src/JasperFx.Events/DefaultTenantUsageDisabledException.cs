namespace JasperFx.Events;

/// <summary>
///     Thrown when a session or projection daemon is created against the default tenant while the
///     store's default tenant usage is disabled — which is the automatic state once a
///     database-per-tenant tenancy is configured.
/// </summary>
/// <remarks>
///     Lifted from the byte-identically-messaged exceptions in Marten and Polecat. Stores subclass
///     or type-forward to this. The single-argument constructor deliberately <em>appends</em> to the
///     standard prefix rather than replacing it, exactly as both store copies did.
/// </remarks>
public class DefaultTenantUsageDisabledException : Exception
{
    public DefaultTenantUsageDisabledException()
        : base(
            $"Default tenant {StorageConstants.DefaultTenantId} usage is disabled. Ensure to create a session by explicitly passing a non-default tenant in the method arg or SessionOptions.")
    {
    }

    public DefaultTenantUsageDisabledException(string message)
        : base($"Default tenant {StorageConstants.DefaultTenantId} usage is disabled. {message}")
    {
    }
}
