using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

/// <summary>
/// Represents a "compacted" stream snapshot of this event stream
/// </summary>
/// <param name="Snapshot"></param>
/// <param name="PreviousStreamId"></param>
/// <param name="PreviousStreamKey"></param>
/// <typeparam name="T"></typeparam>
public record Compacted<T>(T Snapshot, Guid PreviousStreamId, string PreviousStreamKey)
{
    public static (T?, IReadOnlyList<IEvent>) MaybeFastForward(T? snapshot, IReadOnlyList<IEvent> events)
    {
        var index = events.GetLastIndex(e => e.Data is Compacted<T>);
        if (index < 0) return (snapshot, events);

        var compacted = events[index].As<IEvent<Compacted<T>>>();
        return (compacted.Data.Snapshot, events.Skip(index + 1).ToList());
    }
}