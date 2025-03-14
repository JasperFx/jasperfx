namespace JasperFx.Events.Grouping;

public abstract class FanOutOperator<TSource>: IFanOutRule
{
    public FanoutMode Mode { get; set; } = FanoutMode.AfterGrouping;

    public Type OriginatingType => typeof(TSource);

    public abstract IReadOnlyList<IEvent> Apply(IReadOnlyList<IEvent> events);
}

public class FanOutEventDataOperator<TSource, TTarget>: FanOutOperator<TSource>
{
    private readonly Func<TSource, IEnumerable<TTarget>> _fanOutFunc;

    public FanOutEventDataOperator(Func<TSource, IEnumerable<TTarget>> fanOutFunc)
    {
        _fanOutFunc = fanOutFunc;
    }

    public override IReadOnlyList<IEvent> Apply(IReadOnlyList<IEvent> events)
    {
        var raw = events as List<IEvent> ?? events.ToList();
        raw.FanOut(_fanOutFunc);
        return raw;
    }
}

public class FanOutEventOperator<TSource, TTarget>: FanOutOperator<TSource>
{
    private readonly Func<IEvent<TSource>, IEnumerable<TTarget>> _fanOutFunc;

    public FanOutEventOperator(Func<IEvent<TSource>, IEnumerable<TTarget>> fanOutFunc)
    {
        _fanOutFunc = fanOutFunc;
    }

    public override IReadOnlyList<IEvent> Apply(IReadOnlyList<IEvent> events)
    {
        var raw = events as List<IEvent> ?? events.ToList();
        raw.FanOut(_fanOutFunc);
        return raw;
    }
}
