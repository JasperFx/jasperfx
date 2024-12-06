namespace JasperFx.Events.Grouping;

public class EventSlicer<TDoc, TId>: IEventSlicer<TDoc, TId>
{
    private readonly List<Action<SliceGroup<TDoc, TId>, IReadOnlyList<IEvent>>> _configurations = new();
    private readonly List<IFanOutRule> _afterGroupingFanoutRules = new();
    private readonly List<IFanOutRule> _beforeGroupingFanoutRules = new();

    public ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping)
    {
        grouping.ApplyFanOutRules(_beforeGroupingFanoutRules);

        foreach (var configuration in _configurations)
        {
            configuration(grouping, events);
        }

        grouping.ApplyFanOutRules(_afterGroupingFanoutRules);

        return new ValueTask();
    }

    public bool HasAnyRules()
    {
        return _configurations.Any();
    }

    public IEnumerable<Type> DetermineEventTypes()
    {
        foreach (var rule in _beforeGroupingFanoutRules) yield return rule.OriginatingType;

        foreach (var rule in _afterGroupingFanoutRules) yield return rule.OriginatingType;
    }

    public EventSlicer<TDoc, TId> Identity<TEvent>(Func<TEvent, TId> identityFunc)
    {
        _configurations.Add((group, events) => group.AddEvents(identityFunc, events));

        return this;
    }

    public EventSlicer<TDoc, TId> Identities<TEvent>(Func<TEvent, IReadOnlyList<TId>> identitiesFunc)
    {
        _configurations.Add((group, events) => group.AddEvents(identitiesFunc, events));

        return this;
    }

    /// <summary>
    ///     Apply "fan out" operations to the given TEvent type that inserts an enumerable of TChild events right behind the
    ///     parent
    ///     event in the event stream
    /// </summary>
    /// <param name="fanOutFunc"></param>
    /// <param name="mode">Should the fan out operation happen after grouping, or before? Default is after</param>
    /// <typeparam name="TEvent"></typeparam>
    /// <typeparam name="TChild"></typeparam>
    public EventSlicer<TDoc, TId> FanOut<TEvent, TChild>(Func<TEvent, IEnumerable<TChild>> fanOutFunc,
        FanoutMode mode = FanoutMode.AfterGrouping)
    {
        return FanOut(new FanOutEventDataOperator<TEvent, TChild>(fanOutFunc) { Mode = mode }, mode);
    }

    /// <summary>
    ///     Apply "fan out" operations to the given TEvent type that inserts an enumerable of TChild events right behind the
    ///     parent
    ///     event in the event stream
    /// </summary>
    /// <param name="fanOutFunc"></param>
    /// <param name="mode">Should the fan out operation happen after grouping, or before? Default is after</param>
    /// <typeparam name="TEvent"></typeparam>
    /// <typeparam name="TChild"></typeparam>
    public EventSlicer<TDoc, TId> FanOut<TEvent, TChild>(Func<IEvent<TEvent>, IEnumerable<TChild>> fanOutFunc, FanoutMode mode = FanoutMode.AfterGrouping)
    {
        return FanOut(new FanOutEventOperator<TEvent, TChild>(fanOutFunc) { Mode = mode }, mode);
    }

    private EventSlicer<TDoc, TId> FanOut(IFanOutRule fanout, FanoutMode mode)
    {
        switch (mode)
        {
            case FanoutMode.AfterGrouping:
                _afterGroupingFanoutRules.Add(fanout);
                break;

            case FanoutMode.BeforeGrouping:
                _beforeGroupingFanoutRules.Add(fanout);
                break;
        }

        return this;
    }
}
