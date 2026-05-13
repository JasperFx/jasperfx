#nullable enable
using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: instantiates SliceGroup<TDoc, TId> which uses ValueTypeInfo on TId; TId/TDoc preserved at the registration boundary on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic type-argument TDoc flows into SliceGroup<TDoc,...>. Preserved by registration.")]
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

    public ValueTask<IReadOnlyList<object>> SliceAsync(EventRange range)
    {
        var grouping = new SliceGroup<TDoc, string>(StorageConstants.DefaultTenantId)
        {
            Upstream = range.Upstream
        };
        
        grouping.AddEvents<IEvent>(e => e.TenantId, range.Events);
        return new ValueTask<IReadOnlyList<object>>([grouping]);
    }
}

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: instantiates SliceGroup<TDoc, TId> + uses ValueTypeInfo on TId for strong-typed-id unwrap. TId/TDoc preserved at the registration boundary on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic type-argument TId flows into ValueTypeInfo.ForType(typeof(TId)). Preserved by registration.")]
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

    public ValueTask<IReadOnlyList<object>> SliceAsync(EventRange range)
    {
        var grouping = new SliceGroup<TDoc, TId>(StorageConstants.DefaultTenantId)
        {
            Upstream = range.Upstream
        };
        
        grouping.AddEvents<IEvent>(e => _wrapper(e.TenantId), range.Events);
        return new ValueTask<IReadOnlyList<object>>([grouping]);
    }
}

