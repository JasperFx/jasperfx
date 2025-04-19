namespace JasperFx.Events.Grouping;

// Assumption will be that there's a per-tenant wrapper around this
public class ByStream<TDoc, TId> : IEventSlicer<TDoc, TId>, ISingleStreamSlicer where TId : notnull
{
    private readonly Func<IEvent, TId> _identity;

    public ByStream()
    {
        _identity = IEvent.CreateAggregateIdentitySource<TId>();
    }

    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping)
    {
        foreach (var e in events) grouping.AddEvent(_identity(e), e);

        return new ValueTask();
    }
}