#nullable enable
using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events;


public interface IEventSlice
{
    string TenantId { get; }
    IReadOnlyList<IEvent> Events();
}

public interface IEventSlice<T>: IEventSlice
{
    void AppendEvent<TEvent>(Guid streamId, TEvent @event) where TEvent : notnull;
    void AppendEvent<TEvent>(string streamKey, TEvent @event) where TEvent : notnull;
    void AppendEvent<TEvent>(TEvent @event) where TEvent : notnull;
    void PublishMessage(object message);

    /// <summary>
    /// The current snapshot of this projected aggregate
    /// </summary>
    T? Snapshot { get; }
    
    /// <summary>
    /// The current snapshot of this projected aggregate
    /// </summary>
    [Obsolete("Use Snapshot instead")]
    T? Aggregate { get; }

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
    private List<IEvent> _events = new();

    private static readonly Action<IEvent, TId> _identitySetter;
    
    static EventSlice()
    {
        if (typeof(TId) == typeof(Guid))
        {
            _identitySetter = (e, id) => e.StreamId = id!.As<Guid>();
        }
        else if (typeof(TId) == typeof(string))
        {
            _identitySetter = (e, id) => e.StreamKey = id!.As<string>();
        }
        else if (typeof(TId).IsSimple())
        {
            // Can't do anything, can't use this for a single stream projection
            _identitySetter = (_, _) => { };
        }
        else
        {
            // ValueTypeInfo
            var valueType = ValueTypeInfo.ForType(typeof(TId));
            if (valueType.SimpleType == typeof(Guid))
            {
                var unwrapper = valueType.UnWrapper<TId, Guid>();
                _identitySetter = (e, id) => e.StreamId = unwrapper(id);
            }
            else if (valueType.SimpleType == typeof(string))
            {
                var unwrapper = valueType.UnWrapper<TId, string>();
                _identitySetter = (e, id) => e.StreamKey = unwrapper(id);
            }
            else
            {
                // Can't do anything, can't use this for a single stream projection
                _identitySetter = (_, _) => { };
            }
        }
    }

    public EventSlice(TId id, string tenantId, IEnumerable<IEvent>? events = null)
    {
        Id = id;
        TenantId = tenantId;
        if (events != null)
        {
            _events.AddRange(events);
        }
    }

    public EventSlice(TId id, IMetadataContext querySession, IEnumerable<IEvent>? events = null): this(id,
        querySession.TenantId, events)
    {
    }

    internal void FastForwardForCompacting()
    {
        var (snapshot, events) = Compacted<TDoc>.MaybeFastForward(Snapshot, _events);
        Snapshot = snapshot;
        _events = (List<IEvent>)events;
    }

    private readonly StreamActionType? _actionType;

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
    ///     The aggregate identity
    /// </summary>
    public TId Id { get; }

    public List<IEvent>? RaisedEvents { get; private set; }
    public List<object>? PublishedMessages { get; private set; }

    void IEventSlice<TDoc>.AppendEvent<TEvent>(Guid streamId, TEvent @event)
    {
        RaisedEvents ??= new();
        RaisedEvents.Add(new Event<TEvent>(@event)
        {
            StreamId = streamId
        });
    }

    void IEventSlice<TDoc>.AppendEvent<TEvent>(string streamKey, TEvent @event)
    {
        RaisedEvents ??= new();
        RaisedEvents.Add(new Event<TEvent>(@event)
        {
            StreamKey = streamKey
        });
    }

    void IEventSlice<TDoc>.AppendEvent<TEvent>(TEvent @event)
    {
        RaisedEvents ??= new();
        var e = new Event<TEvent>(@event);
        _identitySetter(e, Id);

        RaisedEvents.Add(e);
    }

    void IEventSlice<TDoc>.PublishMessage(object message)
    {
        PublishedMessages ??= new();
        PublishedMessages.Add(message);
    }

    /// <summary>
    ///     The related aggregate document
    /// </summary>
    public TDoc? Snapshot { get; set; }

    /// <summary>
    ///     The related aggregate document
    /// </summary>
    [Obsolete("Use Snapshot instead")]
    public TDoc? Aggregate
    {
        get => Snapshot;
        set => Snapshot = value;
    }

    public string TenantId { get; }

    IEnumerable<IEvent> IEventSlice<TDoc>.RaisedEvents()
    {
        if (RaisedEvents == null) yield break;

        foreach (var @event in RaisedEvents)
        {
            yield return @event;
        }
    }

    IEnumerable<object> IEventSlice<TDoc>.PublishedMessages()
    {
        return PublishedMessages ?? [];
    }

    public int Count => _events.Count;

    int IComparer<IEvent>.Compare(IEvent? x, IEvent? y)
    {
        return x!.Sequence.CompareTo(y!.Sequence);
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

    public void FanOut<TSource, TChild>(Func<TSource, IEnumerable<TChild>> fanOutFunc) where TSource : notnull where TChild : notnull
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

    public void BuildOperations(
        IEventRegistry eventGraph,
        IProjectionBatch storage, 
        AggregationScope aggregationScope)
    {
        if (RaisedEvents == null) return;

        foreach (var e in RaisedEvents)
        {
            var mapping = eventGraph.EventMappingFor(e.EventType);
            e.DotNetTypeName = mapping.DotNetTypeName;
            e.EventTypeName = mapping.EventTypeName;
            e.TenantId = TenantId;
            e.Timestamp = eventGraph.TimeProvider.GetUtcNow();
            // Dont assign e.Id so StreamAction.Append can auto assign a CombGuid
        }

        if (eventGraph.StreamIdentity == StreamIdentity.AsGuid)
        {
            var groups = RaisedEvents
                .GroupBy(x => x.StreamId);

            foreach (var group in groups)
            {
                var action = StreamAction.Append(group.Key, RaisedEvents.ToArray());
                action.TenantId = TenantId;

                if (aggregationScope == AggregationScope.SingleStream && ActionType == StreamActionType.Start)
                {
                    var version = _events.Count;
                    action.ExpectedVersionOnServer = version;

                    foreach (var @event in RaisedEvents)
                    {
                        @event.Version = ++version;
                        storage.QuickAppendEventWithVersion(action, @event);
                    }

                    action.Version = version;

                    storage.UpdateStreamVersion(action);
                }
                else
                {
                    action.TenantId = TenantId;
                    storage.QuickAppendEvents(action);
                }
            }
        }
        else
        {
            var groups = RaisedEvents
                .GroupBy(x => x.StreamKey);

            foreach (var group in groups)
            {
                var action = StreamAction.Append(group.Key, RaisedEvents.ToArray());
                action.TenantId = TenantId;

                if (aggregationScope == AggregationScope.SingleStream && ActionType == StreamActionType.Start)
                {
                    var version = _events.Count;
                    action.ExpectedVersionOnServer = version;

                    foreach (var @event in RaisedEvents)
                    {
                        @event.Version = ++version;
                        storage.QuickAppendEventWithVersion(action, @event);
                    }

                    action.Version = version;

                    storage.UpdateStreamVersion(action);
                }
                else
                {
                    action.TenantId = TenantId;
                    storage.QuickAppendEvents(action);
                }
            }
        }
    }

    /// <summary>
    /// Replace the supplied event within the slice with the new event data
    /// but with a copy of the original event's metadata. This is meant for
    /// event enrichment within the execution of projections 
    /// </summary>
    /// <param name="event"></param>
    /// <param name="data"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public IEvent ReplaceEvent<T>(IEvent @event, T data)
    {
        var index = _events.IndexOf(@event);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(@event), "Cannot find this event within the slice");

        var copy = @event.CloneEventWithNewData(data);
        _events[index] = copy;

        return copy;
    }
    
    /// <summary>
    /// Replace the supplied event within the slice with the new event data
    /// but with a copy of the original event's metadata. This is meant for
    /// event enrichment within the execution of projections 
    /// </summary>
    /// <param name="event"></param>
    /// <param name="data"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public IEvent ReplaceEvent<T>(int index, T data)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));

        var @event = _events[index];

        var copy = @event.CloneEventWithNewData(data);
        _events[index] = copy;

        return copy;
    }

    /// <summary>
    /// Add a reference to the entity of type T to this EventSlice
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    public void Reference<T>(T entity)
    {
        var @event = Event.For(new References<T>(entity));
        _events.Add(@event);
    }

    public ActionType ResultingAction { get; private set; } = Daemon.ActionType.Nothing;

    public void RecordAction(ActionType action)
    {
        ResultingAction = action;
    }
}

