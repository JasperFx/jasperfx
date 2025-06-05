#nullable enable
using JasperFx.Core.Reflection;

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

public class TenantRollupSlicer<TDoc, TId>: IEventSlicer<TDoc, TId>, IEventSlicer where TId : notnull
{
    private readonly Func<string,TId> _wrapper;

    public TenantRollupSlicer()
    {
        _wrapper = ValueTypeInfo.ForType(typeof(TId)).CreateWrapper<TId, string>();
    }

    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping)
    {
        grouping.AddEvents<IEvent>(e => _wrapper(e.TenantId), events);
        return new ValueTask();
    }

    public ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        var grouping = new SliceGroup<TDoc, TId>(StorageConstants.DefaultTenantId);
        grouping.AddEvents<IEvent>(e => _wrapper(e.TenantId), events);
        return new ValueTask<IReadOnlyList<object>>([grouping]);
    }
}

