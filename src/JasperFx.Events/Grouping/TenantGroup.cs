namespace JasperFx.Events.Grouping;

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