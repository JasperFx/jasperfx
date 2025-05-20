namespace JasperFx.MultiTenancy;

public class UnknownTenantIdException: Exception
{
    public UnknownTenantIdException(string tenantId): base($"Unknown tenant id '{tenantId}'")
    {
    }
}
