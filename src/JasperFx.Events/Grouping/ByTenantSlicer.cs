namespace JasperFx.Events.Grouping;

[Obsolete("Not sure this is necessary")]
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