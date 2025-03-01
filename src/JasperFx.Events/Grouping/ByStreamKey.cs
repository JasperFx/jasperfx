#nullable enable
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Grouping;

/// <summary>
///     Slicer strategy by stream key (string identified streams)
/// </summary>
/// <typeparam name="TDoc"></typeparam>
public class ByStreamKey<TDoc>: IEventSlicer<TDoc, string>
{
    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, string> grouping)
    {
        // Assume that you are only processing one tenant at a time
        var groups = events.GroupBy(x => x.StreamKey);
        foreach (var group in groups)
        {
            grouping.AddEvents(group.Key, group);
        }
        
        return new ValueTask();
    }
}

/// <summary>
///     Slicer strategy by stream key (string identified streams) for strong typed identifiers
/// </summary>
/// <typeparam name="TDoc"></typeparam>
public class ByStreamKey<TDoc, TId>
{
    private readonly Func<string,TId> _converter;

    public ByStreamKey(ValueTypeInfo valueType)
    {
        _converter = valueType.CreateConverter<TId, string>();
    }

    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping)
    {
        // Assume that you are only processing one tenant at a time
        var groups = events.GroupBy(x => x.StreamKey);
        foreach (var group in groups)
        {
            grouping.AddEvents(_converter(group.Key), group);
        }
        
        return new ValueTask();
    }
}

