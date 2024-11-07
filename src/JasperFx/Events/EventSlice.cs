#nullable enable
using JasperFx.Core.Reflection;
using JasperFx.Events.Grouping;

namespace JasperFx.Events;

public interface IEventSlice
{
    string TenantId { get; }
    IReadOnlyList<IEvent> Events();
}

public interface IEventSlice<T>: IEventSlice
{
    T? Aggregate { get; }

    /// <summary>
    ///     Is this action the start of a new stream or appending
    ///     to an existing stream?
    /// </summary>
    /// <remarks>
    ///     Default's to determining from the version of the first event on
    ///     stream, but can be overridden so that the value works with
    ///     QuickAppend
    /// </remarks>
    StreamActionType ActionType { get; init; }

    void AppendEvent<TEvent>(Guid streamId, TEvent @event);
    void AppendEvent<TEvent>(string streamKey, TEvent @event);
    void AppendEvent<TEvent>(TEvent @event);
    void PublishMessage(object message);

    IEnumerable<IEvent> RaisedEvents();
    IEnumerable<object> PublishedMessages();
}

/// <summary>
///     A grouping of events that will be applied to an aggregate of type TDoc
///     with the identity TId
/// </summary>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
public class EventSlice<TDoc, TId>: IComparer<IEvent>, IEventSlice<TDoc>
{
    private readonly StreamActionType? _actionType;
    private readonly List<IEvent> _events = new();

    public EventSlice(TId id, string tenantId, IEnumerable<IEvent>? events = null)
    {
        Id = id;
        TenantId = tenantId;
        if (events != null)
        {
            _events.AddRange(events);
        }
    }

    /// <summary>
    ///     The aggregate identity
    /// </summary>
    public TId Id { get; }

    public List<IEvent>? RaisedEvents { get; private set; }
    public List<object>? PublishedMessages { get; private set; }

    public int Count => _events.Count;

    int IComparer<IEvent>.Compare(IEvent x, IEvent y)
    {
        return x.Sequence.CompareTo(y.Sequence);
    }

    /// <summary>
    ///     Is this action the start of a new stream or appending
    ///     to an existing stream?
    /// </summary>
    /// <remarks>
    ///     Default's to determining from the version of the first event on
    ///     stream, but can be overridden so that the value works with
    ///     QuickAppend
    /// </remarks>
    public StreamActionType ActionType
    {
        get => _actionType ?? (_events[0].Version == 1 ? StreamActionType.Start : StreamActionType.Append);
        init => _actionType = value;
    }

    /// <summary>
    ///     The current tenant
    /// </summary>
    public string TenantId { get; }

    void IEventSlice<TDoc>.AppendEvent<TEvent>(Guid streamId, TEvent @event)
    {
        RaisedEvents ??= new List<IEvent>();
        RaisedEvents.Add(new Event<TEvent>(@event) { StreamId = streamId });
    }

    void IEventSlice<TDoc>.AppendEvent<TEvent>(string streamKey, TEvent @event)
    {
        RaisedEvents ??= new List<IEvent>();
        RaisedEvents.Add(new Event<TEvent>(@event) { StreamKey = streamKey });
    }

    void IEventSlice<TDoc>.AppendEvent<TEvent>(TEvent @event)
    {
        RaisedEvents ??= new List<IEvent>();
        var e = new Event<TEvent>(@event);
        if (typeof(TId) == typeof(string))
        {
            e.StreamKey = Id.As<string>();
        }
        else if (typeof(TId) == typeof(Guid))
        {
            e.StreamId = Id.As<Guid>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Cannot determine the stream id for published events for the identity type {typeof(TId).FullNameInCode()}. You will need to explicitly supply the stream id/key");
        }

        RaisedEvents.Add(e);
    }

    void IEventSlice<TDoc>.PublishMessage(object message)
    {
        PublishedMessages ??= new List<object>();
        PublishedMessages.Add(message);
    }

    /// <summary>
    ///     The related aggregate document
    /// </summary>
    public TDoc? Aggregate { get; set; }

    IEnumerable<IEvent> IEventSlice<TDoc>.RaisedEvents()
    {
        if (RaisedEvents == null)
        {
            yield break;
        }

        foreach (var @event in RaisedEvents) yield return @event;
    }

    IEnumerable<object> IEventSlice<TDoc>.PublishedMessages()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    ///     All the events in this slice
    /// </summary>
    public IReadOnlyList<IEvent> Events()
    {
        return _events;
    }

    /// <summary>
    ///     Add a single event to this slice
    /// </summary>
    /// <param name="e"></param>
    public void AddEvent(IEvent e)
    {
        _events.Add(e);
    }

    /// <summary>
    ///     Add a grouping of events to this slice
    /// </summary>
    /// <param name="events"></param>
    public void AddEvents(IEnumerable<IEvent> events)
    {
        _events.AddRange(events);
    }

    /// <summary>
    ///     Iterate through just the event data
    /// </summary>
    /// <returns></returns>
    public IEnumerable<object> AllData()
    {
        foreach (var @event in _events) yield return @event.Data;
    }

    public void FanOut<TSource, TChild>(Func<TSource, IEnumerable<TChild>> fanOutFunc)
    {
        reorderEvents();
        _events.FanOut(fanOutFunc);
    }

    public void ApplyFanOutRules(IEnumerable<IFanOutRule> rules)
    {
        // Need to do this first before applying the fanout rules
        reorderEvents();

        foreach (var rule in rules) rule.Apply(_events);
    }

    private void reorderEvents()
    {
        var events = _events.Distinct().OrderBy(x => x.Sequence).ToArray();
        _events.Clear();
        _events.AddRange(events);
    }
}
