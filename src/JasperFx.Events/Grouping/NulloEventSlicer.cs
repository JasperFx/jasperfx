using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;

public class NulloEventSlicer : IEventSlicer
{
    public ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        return new ValueTask<IReadOnlyList<object>>([events]);
    }

    public ValueTask<IReadOnlyList<object>> SliceAsync(EventRange range)
    {
        return new ValueTask<IReadOnlyList<object>>([range.Events]);
    }
}