using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;

public class EventSlicer<TDoc, TId>: IEventSlicer<TDoc, TId> where TId : notnull
{
    private readonly List<Action<SliceGroup<TDoc, TId>, IReadOnlyList<IEvent>>> _configurations = new();
    private readonly List<IFanOutRule> _afterGroupingFanoutRules = new();
    private readonly List<IFanOutRule> _beforeGroupingFanoutRules = new();
    private readonly IdentityRuleLog _rules = new();

    /// <summary>
    /// marten#6003: how a projection using this slicer finds one identity's events, judged from its grouping rules.
    /// </summary>
    public IdentityAccess IdentityAccess => _rules.Classify();

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

    public EventSlicer<TDoc, TId> Identity<TEvent>(Func<TEvent, TId> identityFunc) where TEvent : notnull
    {
        _configurations.Add((group, events) => group.AddEvents(identityFunc, events));
        _rules.Delegate($"Identity<{typeof(TEvent).ShortNameInCode()}>(...) is a delegate and");

        return this;
    }

    public EventSlicer<TDoc, TId> Identities<TEvent>(Func<TEvent, IReadOnlyList<TId>> identitiesFunc) where TEvent : notnull
    {
        _configurations.Add((group, events) => group.AddEvents(identitiesFunc, events));
        _rules.Delegate($"Identities<{typeof(TEvent).ShortNameInCode()}>(...) is a delegate and");

        return this;
    }

    /// <summary>
    /// marten#6003: group each <typeparamref name="TEvent" /> by the stream it was appended to. See
    /// <see cref="EventSlicer{TDoc,TId,TQuerySession}.IdentityFromStream{TEvent}" />.
    /// </summary>
    public EventSlicer<TDoc, TId> IdentityFromStream<TEvent>() where TEvent : notnull
    {
        var rule = IdentityRules.FromStream<TEvent, TId>();
        _configurations.Add((group, events) => rule(events, group));
        _rules.Stream(typeof(TEvent));

        return this;
    }

    /// <summary>
    /// marten#6003: group every event carrying a stored DCB tag of type <typeparamref name="TTag" /> by that tag's
    /// value. See <see cref="EventSlicer{TDoc,TId,TQuerySession}.IdentityFromTag{TTag}" />.
    /// </summary>
    public EventSlicer<TDoc, TId> IdentityFromTag<TTag>() where TTag : notnull
    {
        var rule = IdentityRules.FromTag<TTag, TId>();
        _configurations.Add((group, events) => rule(events, group));
        _rules.Tag(typeof(TTag));

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
        FanoutMode mode = FanoutMode.AfterGrouping) where TEvent : notnull where TChild : notnull
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
    public EventSlicer<TDoc, TId> FanOut<TEvent, TChild>(Func<IEvent<TEvent>, IEnumerable<TChild>> fanOutFunc, FanoutMode mode = FanoutMode.AfterGrouping) where TEvent : notnull where TChild : notnull
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
