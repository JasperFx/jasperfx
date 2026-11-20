using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;

public interface IEventSlicer
{
    ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events);
    ValueTask<IReadOnlyList<object>> SliceAsync(EventRange range);
}

public class AcrossTenantSlicer<TDoc, TId, TQuerySession> : IEventSlicer where TId : notnull
{
    private readonly TQuerySession _session;
    private readonly IEventSlicer<TDoc, TId, TQuerySession> _inner;

    public AcrossTenantSlicer(TQuerySession session, IEventSlicer<TDoc, TId, TQuerySession> inner)
    {
        _session = session;
        _inner = inner;
    }
    
    public async ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
    {
        var grouping = new SliceGroup<TDoc, TId>(StorageConstants.DefaultTenantId);
        await _inner.SliceAsync(_session, events, grouping);
        return [grouping];
    }

    public async ValueTask<IReadOnlyList<object>> SliceAsync(EventRange range)
    {
        var grouping = new SliceGroup<TDoc, TId>(StorageConstants.DefaultTenantId)
        {
            Upstream = range.Upstream
        };
        
        await _inner.SliceAsync(_session, range.Events, grouping);
        return [grouping];
    }
}

public interface IEventSlicer<TDoc, TId> where TId : notnull
{
    /// <summary>
    ///     This is called by the asynchronous projection runner
    /// </summary>
    /// <param name="events">List of new events within the current event range (page) that is currently being processed by the projection</param>
    /// <param name="grouping"></param>
    /// <returns></returns>
    ValueTask SliceAsync(IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping);
}

public interface IEventSlicer<TDoc, TId, TQuerySession> where TId : notnull
{
    /// <summary>
    ///     This is called by the asynchronous projection runner
    /// </summary>
    /// <param name="events">List of new events within the current event range (page) that is currently being processed by the projection</param>
    /// <param name="grouping"></param>
    /// <returns></returns>
    ValueTask SliceAsync(TQuerySession session, IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping);
}

/// <summary>
///     Plugin point to create custom event to aggregate grouping that requires database lookup
///     as part of the sorting of events into aggregate slices
/// </summary>
/// <typeparam name="TId"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
public interface IJasperFxAggregateGrouper<out TId, in TQuerySession>
{
    /// <summary>
    ///     Apply custom grouping rules to apply events to one or many aggregates
    /// </summary>
    /// <param name="session"></param>
    /// <param name="events"></param>
    /// <param name="grouping"></param>
    /// <returns></returns>
    Task Group(TQuerySession session, IReadOnlyList<IEvent> events, IEventGrouping<TId> grouping);
}

internal class LambdaAggregateGrouper<TId, TQuerySession> : IJasperFxAggregateGrouper<TId, TQuerySession>
{
    private readonly Func<TQuerySession, IReadOnlyList<IEvent>, IEventGrouping<TId>, Task> _func;

    public LambdaAggregateGrouper(Func<TQuerySession, IReadOnlyList<IEvent>, IEventGrouping<TId>, Task> func)
    {
        _func = func;
    }

    public Task Group(TQuerySession session, IReadOnlyList<IEvent> events, IEventGrouping<TId> grouping)
    {
        return _func(session, events, grouping);
    }
}


public class EventSlicer<TDoc, TId, TQuerySession>: IEventSlicer<TDoc, TId, TQuerySession> where TId : notnull
{
    private readonly List<IFanOutRule> _afterGroupingFanoutRules = new();
    private readonly List<IFanOutRule> _beforeGroupingFanoutRules = new();
    private readonly List<Action<IReadOnlyList<IEvent>, IEventGrouping<TId>>> _groupers = new();
    private readonly List<IJasperFxAggregateGrouper<TId, TQuerySession>> _lookupGroupers = new();
    private readonly IdentityRuleLog _rules = new();

    /// <summary>
    /// marten#6003: how a projection using this slicer finds one identity's events, judged from its grouping rules.
    /// </summary>
    public IdentityAccess IdentityAccess => _rules.Classify();

    public async ValueTask SliceAsync(TQuerySession session, IReadOnlyList<IEvent> events, SliceGroup<TDoc, TId> grouping)
    {
        foreach (var fanOutRule in _beforeGroupingFanoutRules)
        {
            events = fanOutRule.Apply(events);
        }
        
        foreach (var grouper in _groupers)
        {
            grouper(events, grouping);
        }

        foreach (var lookupGrouper in _lookupGroupers)
        {
            await lookupGrouper.Group(session, events, grouping).ConfigureAwait(false);
        }

        foreach (var slice in grouping.Slices)
        {
            slice.ApplyFanOutRules(_afterGroupingFanoutRules);
        }
    }

    internal bool HasAnyRules()
    {
        return _groupers.Any() || _lookupGroupers.Any();
    }

    // marten#6003: what the identity source of a multi-stream projection needs to know about its grouping
    internal IdentityRuleLog Rules => _rules;

    internal bool HasLookupGrouping => _lookupGroupers.Count > 0;

    internal bool HasBeforeGroupingFanOut => _beforeGroupingFanoutRules.Count > 0;

    // marten#6003: a slice index replayed "as recorded" gives the identity its indexed events directly, and only the
    // fan-out that runs after grouping is applied again
    internal void ApplyAfterGroupingFanOut(EventSlice<TDoc, TId> slice)
    {
        if (_afterGroupingFanoutRules.Count > 0) slice.ApplyFanOutRules(_afterGroupingFanoutRules);
    }

    public IEnumerable<Type> DetermineEventTypes()
    {
        foreach (var rule in _beforeGroupingFanoutRules) yield return rule.OriginatingType;

        foreach (var rule in _afterGroupingFanoutRules) yield return rule.OriginatingType;
    }

    public EventSlicer<TDoc, TId, TQuerySession> Identity<TEvent>(Func<TEvent, TId> identityFunc) where TEvent : notnull
    {
        _groupers.Add((events, grouping) => grouping.AddEvents(identityFunc, events));
        _rules.Delegate($"Identity<{typeof(TEvent).ShortNameInCode()}>(...) is a delegate and");
        return this;
    }

    public EventSlicer<TDoc, TId, TQuerySession> Identities<TEvent>(Func<TEvent, IReadOnlyList<TId>> identitiesFunc) where TEvent : notnull
    {
        _groupers.Add((events, grouping) => grouping.AddEvents(identitiesFunc, events));
        _rules.Delegate($"Identities<{typeof(TEvent).ShortNameInCode()}>(...) is a delegate and");
        return this;
    }

    /// <summary>
    /// marten#6003: group each <typeparamref name="TEvent" /> by the stream it was appended to: the identity is the
    /// event's stream id or key (wrapped when the identity is a strong-typed identifier). Unlike an
    /// <c>Identity&lt;TEvent&gt;(e =&gt; ...)</c> delegate, the daemon can find one identity's events again from this
    /// rule alone, which is what lets an identity be parked, replayed and rebuilt on its own.
    /// </summary>
    public EventSlicer<TDoc, TId, TQuerySession> IdentityFromStream<TEvent>() where TEvent : notnull
    {
        _groupers.Add(IdentityRules.FromStream<TEvent, TId>());
        _rules.Stream(typeof(TEvent));
        return this;
    }

    /// <summary>
    /// marten#6003: group every event that carries a stored DCB tag of type <typeparamref name="TTag" /> by that tag's
    /// value: the identity is the tag, or the simple value it wraps. An event tagged with several values goes to each.
    /// The daemon can find one identity's events again through the tag, which is what lets an identity be parked,
    /// replayed and rebuilt on its own. Declared, never inferred from a <c>TagWith</c> rule.
    /// </summary>
    public EventSlicer<TDoc, TId, TQuerySession> IdentityFromTag<TTag>() where TTag : notnull
    {
        _groupers.Add(IdentityRules.FromTag<TTag, TId>());
        _rules.Tag(typeof(TTag));
        return this;
    }

    /// <summary>
    ///     Apply a custom event grouping strategy for events. This is additive to Identity() or Identities()
    /// </summary>
    /// <param name="grouper"></param>
    public EventSlicer<TDoc, TId, TQuerySession> CustomGrouping(IJasperFxAggregateGrouper<TId, TQuerySession> grouper)
    {
        _lookupGroupers.Add(grouper);
        _rules.Delegate($"Custom grouping ({grouper.GetType().ShortNameInCode()})");

        return this;
    }
    
    /// <summary>
    ///     Apply a custom event grouping strategy for events. This is additive to Identity() or Identities()
    /// </summary>
    /// <param name="grouper"></param>
    public EventSlicer<TDoc, TId, TQuerySession> CustomGrouping(Func<TQuerySession, IReadOnlyList<IEvent>, IEventGrouping<TId>, Task> func)
    {
        _lookupGroupers.Add(new LambdaAggregateGrouper<TId, TQuerySession>(func));
        _rules.Delegate("Custom grouping");

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
    public EventSlicer<TDoc, TId, TQuerySession> FanOut<TEvent, TChild>(Func<TEvent, IEnumerable<TChild>> fanOutFunc,
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
    public EventSlicer<TDoc, TId, TQuerySession> FanOut<TEvent, TChild>(Func<IEvent<TEvent>, IEnumerable<TChild>> fanOutFunc, FanoutMode mode = FanoutMode.AfterGrouping) where TEvent : notnull where TChild : notnull
    {
        return FanOut(new FanOutEventOperator<TEvent, TChild>(fanOutFunc) { Mode = mode }, mode);
    }

    private EventSlicer<TDoc, TId, TQuerySession> FanOut(IFanOutRule fanout, FanoutMode mode)
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


