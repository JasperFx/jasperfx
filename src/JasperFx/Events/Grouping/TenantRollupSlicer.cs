#nullable enable
namespace JasperFx.Events.Grouping;

public class TenantRollupSlicer<TDoc>: IEventSlicer<TDoc, string>
{
    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, string> grouping)
    {
        grouping.AddEvents<IEvent>(e => e.TenantId, events);
        return new ValueTask();
    }
}
