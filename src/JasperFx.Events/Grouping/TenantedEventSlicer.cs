namespace JasperFx.Events.Grouping;

public class TenantedEventSlicer<TDoc, TId> : IEventSlicer
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