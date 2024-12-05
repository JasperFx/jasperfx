using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;


// TODO -- this could do per tenant slicing!
public class TenantedSliceGroup<TDoc, TId> : EventRangeGroup
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

    public async Task ConfigureUpdateBatch(IAggregation aggregation)
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
        
        // TODO -- WHOA, should this screen first for events in a specific tenant id?????????
        var groups = await _slicer.SliceAsyncEvents(Range.Events);
        foreach (var group in groups)
        {
            _groups[group.TenantId] = group;
        }
    }
}