#nullable enable
namespace JasperFx.Events.Grouping;

public class TenantRollupSlicer<TDoc>: IEventSlicer<TDoc, string>, IEventSlicer
{
    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, string> grouping)
    {
        grouping.AddEvents<IEvent>(e => e.TenantId, events);
        return new ValueTask();
    }

    public ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        var grouping = new SliceGroup<TDoc, string>(StorageConstants.DefaultTenantId);
        grouping.AddEvents<IEvent>(e => e.TenantId, events);
        return new ValueTask<IReadOnlyList<object>>([grouping]);
    }
}
