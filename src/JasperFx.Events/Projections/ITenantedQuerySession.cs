namespace JasperFx.Events.Projections;

public interface ITenantedQuerySession<TQuerySession>
{
    TQuerySession ForTenant(string tenantId);
}