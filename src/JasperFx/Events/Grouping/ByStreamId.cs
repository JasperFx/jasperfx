#nullable enable
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Grouping;

/// <summary>
///     Slicer strategy by stream id (Guid identified streams)
/// </summary>
/// <typeparam name="TDoc"></typeparam>
public class ByStreamId<TDoc>: IEventSlicer<TDoc, Guid>
{
    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, Guid> grouping)
    {
        // Assume that you are only processing one tenant at a time
        var groups = events.GroupBy(x => x.StreamId);
        foreach (var group in groups)
        {
            grouping.AddEvents(group.Key, group);
        }
        
        return new ValueTask();
    }
}

/// <summary>
///     Slicer strategy by stream id (Guid identified streams) and a custom value type
/// </summary>
/// <typeparam name="TDoc"></typeparam>
public class ByStreamId<TDoc, TId>: IEventSlicer<TDoc, TId>
{
    private readonly Func<Guid, TId> _converter;

    public ByStreamId(ValueTypeInfo valueType)
    {
        _converter = valueType.CreateConverter<TId, Guid>();
    }

    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping)
    {
        // Assume that you are only processing one tenant at a time
        var groups = events.GroupBy(x => x.StreamId);
        foreach (var group in groups)
        {
            grouping.AddEvents(_converter(group.Key), group);
        }
        
        return new ValueTask();
    }
}

