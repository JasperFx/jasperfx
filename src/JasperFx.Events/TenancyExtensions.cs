using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events;

public static class TenancyExtensions
{
    public static TQuerySession CorrectSessionForTenancy<TQuerySession>(this TQuerySession session, string tenantId)
    {
        if (tenantId.IsEmpty() || tenantId == StorageConstants.DefaultTenantId) return session;

        if (session is ITenantedQuerySession<TQuerySession> tenanted)
        {
            return tenanted.ForTenant(tenantId);
        }

        return session;
    }
}