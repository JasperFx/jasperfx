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

public class TenantedEventSlicer : IEventSlicer
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

public interface IEventSlicer<TDoc, TId>
{
    /// <summary>
    ///     This is called by the asynchronous projection runner
    /// </summary>
    /// <param name="events"></param>
    /// <returns></returns>
    ValueTask<IReadOnlyList<EventSliceGroup<TDoc, TId>>> SliceAsyncEvents(List<IEvent> events);
}