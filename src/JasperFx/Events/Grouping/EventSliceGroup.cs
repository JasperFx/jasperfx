using JasperFx.Core;
using JasperFx.Events.Projections;

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

public interface IAggregation : IProjectionBatch
{
    Task ProcessAggregationAsync<TDoc, TId>(EventSliceGroup<TDoc,TId> group, CancellationToken cancellation);
}

public class TenantedSliceGroup<TDoc, TId> : EventRangeGroup<IAggregation>
{
    private readonly IEventSlicer<TDoc, TId> _slicer;

    private readonly LightweightCache<string, EventSliceGroup<TDoc, TId>> _groups
        = new(tenantId => new(tenantId));

    public TenantedSliceGroup(EventRange range, IEventSlicer<TDoc, TId> slicer) : base(range)
    {
        _slicer = slicer;
    }

    public TenantedSliceGroup(EventRange range, IEventSlicer<TDoc, TId> slicer,
        IReadOnlyList<EventSliceGroup<TDoc, TId>> groups) : this(range, slicer)
    {
        foreach (var group in groups)
        {
            _groups[group.TenantId] = group;
        }
    }
    
    public override string ToString()
    {
        return $"Aggregate for {Range}, {_groups.Count} groups, {_groups.Select(x => x.Slices.Count)} slices";
    }

    public override void Dispose()
    {
    }

    protected override void reset()
    {
    }

    public override async Task ConfigureUpdateBatch(IAggregation aggregation)
    {
        await Parallel.ForEachAsync(_groups, CancellationToken.None,
                async (group, _) =>
                    await aggregation.ProcessAggregationAsync(group, Cancellation).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    public override async ValueTask SkipEventSequence(long eventSequence)
    {
        reset();
        Range.SkipEventSequence(eventSequence);
        
        _groups.Clear();
        var groups = await _slicer.SliceAsyncEvents(Range.Events);
        foreach (var group in groups)
        {
            _groups[group.TenantId] = group;
        }
    }
}




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

    public void ApplyFanOutRules(List<IFanOutRule> fanoutRules)
    {
        foreach (var slice in Slices)
        {
            slice.ApplyFanOutRules(fanoutRules);
        }
    }
}
