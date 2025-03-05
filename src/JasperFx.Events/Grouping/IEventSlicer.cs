using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Grouping;

public interface IEventSlicer
{
    ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events);
}

public class NulloEventSlicer : IEventSlicer
{
    public ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        return new ValueTask<IReadOnlyList<object>>([events]);
    }
}

public class ByTenantSlicer : IEventSlicer
{
    public ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        var groups = Group(events);

        return new ValueTask<IReadOnlyList<object>>(groups);
    }

    public static List<TenantGroup> Group(IReadOnlyList<IEvent> events)
    {
        var groups = events.GroupBy(x => x.TenantId)
            .Select(g => new TenantGroup(g.Key, g)).ToList();
        return groups;
    }
}

/// <summary>
/// A group of events by tenant id
/// </summary>
/// <param name="TenantId"></param>
public class TenantGroup
{
    public string TenantId { get; }
    public IReadOnlyList<IEvent> Events { get; }

    public TenantGroup(string tenantId, IEnumerable<IEvent> events)
    {
        TenantId = tenantId;
        Events = events.ToList();
    }
}

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

public interface IEventSlicer<TDoc, TId> 
{
    /// <summary>
    ///     This is called by the asynchronous projection runner
    /// </summary>
    /// <param name="events">Enumerable of new events within the current event range (page) that is currently being processed by the projection</param>
    /// <param name="grouping"></param>
    /// <returns></returns>
    ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping);
}

public interface ISingleStreamSlicer
{

}

