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

    /// <summary>
    ///     Open the read-only event store tier in one tenant's scope, degrading to the store-global
    ///     session when the store has not implemented
    ///     <see cref="IEventStore.OpenReadOnlyEventStore(string?)" /> (jasperfx#885).
    /// </summary>
    /// <remarks>
    ///     For a caller whose query carries the tenant itself — <c>QueryStreamStates(tenantId)</c>, an
    ///     <see cref="EventQuery.TenantId" /> — the fallback narrows nothing, because the filter is
    ///     applied either way. What the tenant-aware producer buys is reaching the surface <em>at
    ///     all</em> on a store whose default tenant is disabled, where opening the default session
    ///     throws before any filter is applied. Do not use this for a read whose only tenant scope
    ///     would be the session's: there the refusal is the honest answer, so call
    ///     <see cref="IEventStore.OpenReadOnlyEventStore(string?)" /> directly and let it throw.
    /// </remarks>
    public static IReadOnlyEventStore OpenReadOnlyEventStoreOrGlobal(this IEventStore store, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (tenantId == null) return store.OpenReadOnlyEventStore();

        try
        {
            return store.OpenReadOnlyEventStore(tenantId);
        }
        catch (NotSupportedException)
        {
            return store.OpenReadOnlyEventStore();
        }
    }
}