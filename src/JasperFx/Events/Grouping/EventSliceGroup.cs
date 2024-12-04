using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Grouping;

/// <summary>
/// Structure to hold and help organize events in "slices" by identity to apply
/// to the matching aggregate document TDoc
/// </summary>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
public class EventSliceGroup<TDoc, TId>: IEventGrouping<TId>
{
    public LightweightCache<TId, EventSlice<TDoc, TId>> Slices { get; }
    
    public string TenantId { get; }

    public EventSliceGroup(string tenantId)
    {
        TenantId = tenantId;
        Slices = new LightweightCache<TId, EventSlice<TDoc, TId>>(id => new EventSlice<TDoc, TId>(id, tenantId));
    }

    public EventSliceGroup(string tenantId, IEnumerable<EventSlice<TDoc, TId>> slices) : this(tenantId)
    {
        foreach (var slice in slices) Slices[slice.Id] = slice;
    }

    public EventSliceGroup() : this(StorageConstants.DefaultTenantId)
    {
    }
    
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


    public void FanOutOnEach<TSource, TChild>(Func<TSource, IEnumerable<TChild>> fanOutFunc)
    {
        foreach (var slice in Slices)
        {
            slice.FanOut(fanOutFunc);
        }
    }

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

    public void ApplyFanOutRules(List<IFanOutRule> fanoutRules)
    {
        foreach (var slice in Slices)
        {
            slice.ApplyFanOutRules(fanoutRules);
        }
    }
}
