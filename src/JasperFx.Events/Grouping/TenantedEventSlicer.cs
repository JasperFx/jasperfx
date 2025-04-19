using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;

public class TenantedEventSlicer<TDoc, TId> : IEventSlicer where TId : notnull
{
    private readonly IEventSlicer<TDoc, TId> _inner;

    public TenantedEventSlicer(IEventSlicer<TDoc, TId> inner)
    {
        _inner = inner;
    }

    public async ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        var groups = new List<object>();
        var byTenant = ByTenantSlicer.Group(events);
        foreach (var tenantGroup in byTenant)
        {
            var group = new SliceGroup<TDoc, TId>(tenantGroup.TenantId);
            await _inner.SliceAsync(tenantGroup.Events, group);
            
            groups.Add(group);
        }

        return groups;
    }
}

public class TenantedEventSlicer<TDoc, TId, TQuerySession> : IEventSlicer where TId : notnull
{
    private readonly TQuerySession _session;
    private readonly IEventSlicer<TDoc, TId, TQuerySession> _inner;

    public TenantedEventSlicer(TQuerySession session, IEventSlicer<TDoc, TId, TQuerySession> inner)
    {
        _session = session;
        _inner = inner;
    }

    public async ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        var groups = new List<object>();
        var byTenant = ByTenantSlicer.Group(events);
        foreach (var tenantGroup in byTenant)
        {
            var group = new SliceGroup<TDoc, TId>(tenantGroup.TenantId);

            var tenantSession = _session is ITenantedQuerySession<TQuerySession> tenanted
                ? tenanted.ForTenant(tenantGroup.TenantId)
                : _session;
            
            await _inner.SliceAsync(tenantSession, tenantGroup.Events, group);
            
            groups.Add(group);
        }

        return groups;
    }
}