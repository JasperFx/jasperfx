using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Grouping;

/// <summary>
/// Structure to hold and help organize events in "slices" by identity to apply
/// to the matching aggregate document TDoc. Note that TDoc might be a marker type.
/// </summary>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
public class SliceGroup<TDoc, TId> : IEventGrouping<TId>
{
    public LightweightCache<TId, EventSlice<TDoc, TId>> Slices { get; }
    
    public string TenantId { get; }

    public SliceGroup(string tenantId)
    {
        TenantId = tenantId;
        Slices = new LightweightCache<TId, EventSlice<TDoc, TId>>(id => new EventSlice<TDoc, TId>(id, tenantId));
    }

    public SliceGroup(string tenantId, IEnumerable<EventSlice<TDoc, TId>> slices) : this(tenantId)
    {
        foreach (var slice in slices) Slices[slice.Id] = slice;
    }

    public SliceGroup() : this(StorageConstants.DefaultTenantId)
    {
    }

    /// <summary>
    ///     Add events to streams where each event of type TEvent applies to only
    ///     one stream
    /// </summary>
    /// <param name="singleIdSource"></param>
    /// <param name="events"></param>
    /// <typeparam name="TEvent"></typeparam>
    public void AddEvents<TEvent>(Func<TEvent, TId> singleIdSource, IEnumerable<IEvent> events)
    {
        if (typeof(TEvent).Closes(typeof(IEvent<>)))
        {
            var matching = events.OfType<TEvent>();
            foreach (var @event in matching)
            {
                var id = singleIdSource(@event);
                AddEvent(id, (IEvent)@event);
            }
        }
        else if (typeof(TEvent) == typeof(IEvent))
        {
            foreach (var @event in events)
            {
                var id = singleIdSource((TEvent)@event);
                AddEvent(id, @event);
            }
        }
        else
        {
            var matching = events.OfType<IEvent<TEvent>>();
            foreach (var @event in matching)
            {
                var id = singleIdSource(@event.Data);
                AddEvent(id, @event);
            }
        }
    }

    /// <summary>
    ///     Apply "fan out" operations to the given TSource type that inserts an enumerable of TChild events right behind the
    ///     parent
    ///     event in the event stream just after any instance of the parent
    /// </summary>
    /// <param name="fanOutFunc"></param>
    /// <typeparam name="TSource"></typeparam>
    /// <typeparam name="TChild"></typeparam>
    public void FanOutOnEach<TSource, TChild>(Func<TSource, IEnumerable<TChild>> fanOutFunc)
    {
        foreach (var slice in Slices)
        {
            slice.FanOut(fanOutFunc);
        }
    }

    /// <summary>
    ///     Add events to multiple slices where each event of type TEvent may be related to many
    ///     different aggregates
    /// </summary>
    /// <param name="multipleIdSource"></param>
    /// <param name="events"></param>
    /// <typeparam name="TEvent"></typeparam>
    public void AddEvents<TEvent>(Func<TEvent, IEnumerable<TId>> multipleIdSource, IEnumerable<IEvent> events)
    {
        if (typeof(TEvent).Closes(typeof(IEvent<>)))
        {
            var matching = events.OfType<TEvent>();
            foreach (var @event in matching)
            {
                foreach (var id in multipleIdSource(@event))
                {
                    AddEvent(id, (IEvent)@event);
                }
            }
        }
        else
        {
            var matching = events.OfType<IEvent<TEvent>>();
            foreach (var @event in matching)
            {
                foreach (var id in multipleIdSource(@event.Data))
                {
                    AddEvent(id, (IEvent)@event);
                }
            }
        }
    }

    /// <summary>
    ///     Add a single event to a single event slice by id
    /// </summary>
    /// <param name="id">The aggregate id</param>
    /// <param name="event"></param>
    public void AddEvent(TId id, IEvent @event)
    {
        if (id != null)
        {
            Slices[id].AddEvent(@event);
        }

    }

    /// <summary>
    ///     Add many events to a single event slice by aggregate id
    /// </summary>
    /// <param name="id">The aggregate id</param>
    /// <param name="events"></param>
    public void AddEvents(TId id, IEnumerable<IEvent> events)
    {
        if (id != null)
        {
            Slices[id].AddEvents(events);
        }
    }

    public void ApplyFanOutRules(List<IFanOutRule> fanoutRules)
    {
        foreach (var slice in Slices)
        {
            slice.ApplyFanOutRules(fanoutRules);
        }
    }
}
