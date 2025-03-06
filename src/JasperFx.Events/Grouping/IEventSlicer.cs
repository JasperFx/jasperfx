using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Grouping;

public interface IEventSlicer
{
    ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events);
}

public interface IEventSlicer<TDoc, TId> 
{
    /// <summary>
    ///     This is called by the asynchronous projection runner
    /// </summary>
    /// <param name="events">Enumerable of new events within the current event range (page) that is currently being processed by the projection</param>
    /// <param name="grouping"></param>
    /// <returns></returns>
    ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping);
}