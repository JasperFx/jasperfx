namespace JasperFx.Events.Grouping;

public interface IEventSlicer<TDoc, TId>
{
    /// <summary>
    ///     This is called by the asynchronous projection runner
    /// </summary>
    /// <param name="events"></param>
    /// <returns></returns>
    ValueTask<IReadOnlyList<EventSliceGroup<TDoc, TId>>> SliceAsyncEvents(List<IEvent> events);
}