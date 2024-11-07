using JasperFx.Core;

namespace JasperFx.Events.Grouping;

public class EventGrouping<TDoc, TId>: IEventGrouping<TId>
{
    public LightweightCache<TId, EventSlice<TDoc, TId>> Slices { get; }
    
    public string TenantId { get; }

    public EventGrouping(string tenantId)
    {
        TenantId = tenantId;
        Slices = new LightweightCache<TId, EventSlice<TDoc, TId>>(id => new EventSlice<TDoc, TId>(id, tenantId));
    }

    public EventGrouping() : this(StorageConstants.DefaultTenantId)
    {
    }
    
    public void AddEvents<TEvent>(Func<TEvent, TId> singleIdSource, IEnumerable<IEvent> events)
    {
        AddEventsWithMetadata<TEvent>(e => singleIdSource(e.Data), events);
    }

    /// <summary>
    ///     Add events to the grouping based on the outer IEvent<TEvent> envelope type
    /// </summary>
    /// <param name="singleIdSource"></param>
    /// <param name="events"></param>
    /// <typeparam name="TEvent"></typeparam>
    public void AddEventsWithMetadata<TEvent>(Func<IEvent<TEvent>, TId> singleIdSource, IEnumerable<IEvent> events)
    {
        var matching = events.OfType<IEvent<TEvent>>();
        foreach (var @event in matching)
        {
            var id = singleIdSource(@event);
            AddEvent(id, @event);
        }
    }

    public void FanOutOnEach<TSource, TChild>(Func<TSource, IEnumerable<TChild>> fanOutFunc)
    {
        foreach (var slice in Slices) slice.FanOut(fanOutFunc);
    }

    public void AddEvents<TEvent>(Func<TEvent, IEnumerable<TId>> multipleIdSource, IEnumerable<IEvent> events)
    {
        AddEventsWithMetadata<TEvent>(e => multipleIdSource(e.Data), events);
    }

    /// <summary>
    ///     Add events to the grouping based on the outer IEvent<TEvent> envelope type
    /// </summary>
    /// <param name="singleIdSource"></param>
    /// <param name="events"></param>
    /// <typeparam name="TEvent"></typeparam>
    public void AddEventsWithMetadata<TEvent>(Func<IEvent<TEvent>, IEnumerable<TId>> multipleIdSource, IEnumerable<IEvent> events)
    {
        var matching = events.OfType<IEvent<TEvent>>()
            .SelectMany(e => multipleIdSource(e).Select(id => (id, @event: e)));

        var groups = matching.GroupBy(x => x.id);
        foreach (var group in groups) AddEvents(group.Key, group.Select(x => x.@event));
    }

    public void AddEvent(TId id, IEvent @event)
    {
        if (id != null)
        {
            Slices[id].AddEvent(@event);
        }

    }

    public void AddEvents(TId id, IEnumerable<IEvent> events)
    {
        if (id != null)
        {
            Slices[id].AddEvents(events);
        }
    }

}
