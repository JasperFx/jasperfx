using System.Linq.Expressions;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

public enum StreamActionType
{
    /// <summary>
    ///     This is a new stream. This action will be rejected
    ///     if a stream with the same identity exists in the database
    /// </summary>
    Start,

    /// <summary>
    ///     Append these events to an existing stream. If the stream does not
    ///     already exist, it will be created with these events
    /// </summary>
    Append
}

/// <summary>
///     Models a series of events to be appended to either a new or
///     existing stream
/// </summary>
public class StreamAction
{
    private readonly List<IEvent> _events = new();

    public StreamAction(Guid stream, StreamActionType actionType)
    {
        Id = stream;
        ActionType = actionType;
    }

    public StreamAction(string stream, StreamActionType actionType)
    {
        Key = stream;
        ActionType = actionType;
    }

    protected StreamAction(Guid id, string key, StreamActionType actionType)
    {
        Id = id;
        Key = key;
        ActionType = actionType;
    }

    /// <summary>
    ///     Identity of the stream if using Guid's as the identity
    /// </summary>
    public Guid Id { get; internal set; }

    /// <summary>
    ///     The identity of this stream if using strings as the stream
    ///     identity
    /// </summary>
    public string? Key { get; }

    /// <summary>
    ///     Is this action the start of a new stream or appending
    ///     to an existing stream?
    /// </summary>
    public StreamActionType ActionType { get; }

    /// <summary>
    ///     If the stream was started as tagged to an aggregate type, that will
    ///     be reflected in this property. May be null
    /// </summary>
    public Type? AggregateType { get; set; }

    /// <summary>
    ///     JasperFx.Events's name for the aggregate type that will be persisted
    ///     to the streams table
    /// </summary>
    public string? AggregateTypeName { get; internal set; }

    /// <summary>
    ///     The Id of the current tenant
    /// </summary>
    public string TenantId { get; set; } = null!;

    /// <summary>
    ///     The events involved in this action
    /// </summary>
    public IReadOnlyList<IEvent> Events => _events;

    /// <summary>
    ///     The expected *starting* version of the stream in the server. This is used
    ///     to facilitate optimistic concurrency checks
    /// </summary>
    public long? ExpectedVersionOnServer { get; set; }

    /// <summary>
    ///     The ending version of the stream for this action
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    ///     The recorded timestamp for these events
    /// </summary>
    public DateTimeOffset? Timestamp { get; internal set; }

    /// <summary>
    ///     When was the stream created
    /// </summary>
    public DateTimeOffset? Created { get; internal set; }

    public StreamAction AddEvents(IReadOnlyList<IEvent> events)
    {
        _events.EnsureCapacity(_events.Count + events.Count);

        foreach (var @event in events) AddEvent(@event);

        return this;
    }

    public StreamAction AddEvent(IEvent @event)
    {
        if (@event.Id == Guid.Empty)
        {
            @event.Id = CombGuidIdGeneration.NewGuid();
        }

        @event.StreamId = Id;
        @event.StreamKey = Key;

        _events.Add(@event);

        return this;
    }
    
    /// <summary>
    ///     Create a new StreamAction for starting a new stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    /// <exception cref="EmptyEventStreamException"></exception>
    public static StreamAction Start(IEventRegistry graph, Guid streamId, params object[] events)
    {
        if (!events.Any())
        {
            throw new EmptyEventStreamException(streamId);
        }

        return new StreamAction(streamId, StreamActionType.Start).AddEvents(events.Select(graph.BuildEvent).ToArray());
    }

    /// <summary>
    ///     Create a new StreamAction for starting a new stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    /// <exception cref="EmptyEventStreamException"></exception>
    public static StreamAction Start(Guid streamId, params IEvent[] events)
    {
        if (!events.Any())
        {
            throw new EmptyEventStreamException(streamId);
        }

        return new StreamAction(streamId, StreamActionType.Start).AddEvents(events);
    }

    /// <summary>
    ///     Create a new StreamAction for starting a new stream
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    /// <exception cref="EmptyEventStreamException"></exception>
    public static StreamAction Start(IEventRegistry graph, string streamKey, params object[] events)
    {
        if (!events.Any())
        {
            throw new EmptyEventStreamException(streamKey);
        }

        return new StreamAction(streamKey, StreamActionType.Start).AddEvents(events.Select(graph.BuildEvent).ToArray());
    }

    /// <summary>
    ///     Create a new StreamAction for starting a new stream
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    /// <exception cref="EmptyEventStreamException"></exception>
    public static StreamAction Start(string streamKey, params IEvent[] events)
    {
        if (!events.Any())
        {
            throw new EmptyEventStreamException(streamKey);
        }

        return new StreamAction(streamKey, StreamActionType.Start).AddEvents(events);
    }

    /// <summary>
    ///     Create a new StreamAction for appending to an existing stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static StreamAction Append(IEventRegistry graph, Guid streamId, params object[] events)
    {
        var stream = new StreamAction(streamId, StreamActionType.Append);
        stream.AddEvents(events.Select(graph.BuildEvent).ToArray());
        return stream;
    }

    /// <summary>
    ///     Create a new StreamAction for appending to an existing stream
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static StreamAction Append(Guid streamId, IEvent[] events)
    {
        var stream = new StreamAction(streamId, StreamActionType.Append);
        stream.AddEvents(events);
        return stream;
    }

    /// <summary>
    ///     Create a new StreamAction for appending to an existing stream
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static StreamAction Append(IEventRegistry graph, string streamKey, params object[] events)
    {
        var stream = new StreamAction(streamKey, StreamActionType.Append);
        stream._events.AddRange(events.Select(graph.BuildEvent));
        return stream;
    }

    /// <summary>
    ///     Create a new StreamAction for appending to an existing stream
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static StreamAction Append(string streamKey, IEvent[] events)
    {
        var stream = new StreamAction(streamKey, StreamActionType.Append);
        stream._events.AddRange(events.OrderBy(x => x.Version));
        return stream;
    }

    /// <summary>
    ///     Applies versions, .Net type aliases, the reserved sequence numbers, timestamps, etc.
    ///     to get the events ready to be inserted into the mt_events table
    /// </summary>
    /// <param name="currentVersion"></param>
    /// <param name="graph"></param>
    /// <param name="sequences"></param>
    /// <param name="session"></param>
    /// <exception cref="EventStreamUnexpectedMaxEventIdException"></exception>
    public void PrepareEvents(long currentVersion, IEventRegistry graph, Queue<long> sequences, IMetadataContext session)
    {
        var timestamp = graph.TimeProvider.GetUtcNow();

        if (AggregateType != null)
        {
            AggregateTypeName = graph.AggregateAliasFor(AggregateType);
        }

        // Augment the events before checking expected versions, this allows the sequence/etc to properly be set on the resulting tombstone events
        if (graph.AppendMode == EventAppendMode.Rich)
        {
            applyRichMetadata(currentVersion, graph, sequences, session, timestamp);
        }
        else
        {
            applyQuickMetadata(graph, session, timestamp);

            if (ActionType == StreamActionType.Start)
            {
                ExpectedVersionOnServer = 0;
            }

            // In this case, we "know" what the event versions should be, so just set them now
            if (ExpectedVersionOnServer.HasValue)
            {
                var i = ExpectedVersionOnServer.Value;
                foreach (var e in Events) e.Version = ++i;
            }
        }

        if (currentVersion != 0)
        {
            // Guard logic for optimistic concurrency
            if (ExpectedVersionOnServer.HasValue)
            {
                if (currentVersion != ExpectedVersionOnServer.Value)
                {
                    throw new EventStreamUnexpectedMaxEventIdException((object?)Key ?? Id, AggregateType,
                        ExpectedVersionOnServer.Value, currentVersion);
                }
            }

            ExpectedVersionOnServer = currentVersion;
        }
        else if (ExpectedVersionOnServer.HasValue)
        {
            // This is from trying to call Append() with an expected version on a non-existent stream
            if (ExpectedVersionOnServer.Value != 0)
            {
                throw new EventStreamUnexpectedMaxEventIdException((object?)Key ?? Id, AggregateType,
                    ExpectedVersionOnServer.Value, currentVersion);
            }
        }

        Version = Events.Last().Version;
    }

    private void applyQuickMetadata(IEventRegistry graph, IMetadataContext session, DateTimeOffset timestamp)
    {
        foreach (var @event in _events)
        {
            if (@event.Id == default)
            {
                @event.Id = CombGuidIdGeneration.NewGuid();
            }

            @event.TenantId = session.TenantId;
            
            if (@event.Timestamp == default(DateTimeOffset))
            {
                @event.Timestamp = timestamp;
            }

            ProcessMetadata(@event, graph, session);
        }
    }

    private void applyRichMetadata(long currentVersion, IEventRegistry graph, Queue<long> sequences,
        IMetadataContext session,
        DateTimeOffset timestamp)
    {
        var i = currentVersion;
        foreach (var @event in _events)
        {
            @event.Version = ++i;
            if (@event.Id == Guid.Empty)
            {
                @event.Id = CombGuidIdGeneration.NewGuid();
            }

            if (sequences.TryDequeue(out var sequence))
            {
                @event.Sequence = sequence;
            }

            @event.TenantId = session.TenantId;
            
            if (@event.Timestamp == default(DateTimeOffset))
            {
                @event.Timestamp = timestamp;
            }

            ProcessMetadata(@event, graph, session);
        }
    }

    public static StreamAction ForReference(Guid streamId, string tenantId)
    {
        return new StreamAction(streamId, StreamActionType.Append) { TenantId = tenantId };
    }

    public static StreamAction ForReference(string streamKey, string tenantId)
    {
        return new StreamAction(streamKey, StreamActionType.Append) { TenantId = tenantId };
    }

    public static StreamAction ForTombstone(IMetadataContext context)
    {
        return new StreamAction(
            Tombstone.StreamId, Tombstone.StreamKey,
            StreamActionType.Append) { TenantId = context.TenantId };
    }

    private static void ProcessMetadata(IEvent @event, IEventRegistry graph, IMetadataContext metadata)
    {
        if (metadata.CausationIdEnabled)
        {
            @event.CausationId ??= metadata.CausationId;
        }

        if (metadata.CorrelationIdEnabled)
        {
            @event.CorrelationId ??= metadata.CorrelationId;
        }

        if (!metadata.HeadersEnabled)
        {
            return;
        }

        if (!(metadata.Headers?.Count > 0))
        {
            return;
        }

        foreach (var header in metadata.Headers) @event.SetHeader(header.Key, header.Value);
    }

    public static StreamAction For(Guid streamId, IReadOnlyList<IEvent> events)
    {
        var action = events[0].Version == 1 ? StreamActionType.Start : StreamActionType.Append;
        return new StreamAction(streamId, action)
            .AddEvents(events);
    }

    public static StreamAction For(string streamKey, IReadOnlyList<IEvent> events)
    {
        var action = events[0].Version == 1 ? StreamActionType.Start : StreamActionType.Append;
        return new StreamAction(streamKey, action)
            .AddEvents(events);
    }

    public IEnumerable<Event<Tombstone>> ToTombstoneEvents(IEventType eventType, Tombstone tombstone)
    {
        return Events.Select(x => new Event<Tombstone>(tombstone)
        {
            Sequence = x.Sequence,
            Version = x.Sequence, // this is important to avoid clashes on the id/version constraint
            TenantId = TenantId,
            StreamId = Tombstone.StreamId,
            StreamKey = Tombstone.StreamKey,
            Id = CombGuidIdGeneration.NewGuid(),
            EventTypeName = eventType.EventTypeName,
            DotNetTypeName = eventType.DotNetTypeName
        });
    }

    public bool IsStarting()
    {
        return ActionType == StreamActionType.Start || Events.First().Version == 1;
    }
    
    /// <summary>
    /// Build a Func that can resolve an identity from the StreamAction and even
    /// handles the dastardly strong typed identifiers
    /// </summary>
    /// <typeparam name="TId"></typeparam>
    /// <returns></returns>
    public static Func<StreamAction, TId> CreateAggregateIdentitySource<TId>()
        where TId : notnull
    {
        if (typeof(TId) == typeof(Guid)) return e => e.Id.As<TId>();
        if (typeof(TId) == typeof(string)) return e => e.Key!.As<TId>();
        
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(TId));
        
        var e = Expression.Parameter(typeof(StreamAction), "e");
        var eMember = valueTypeInfo.SimpleType == typeof(Guid)
            ? ReflectionHelper.GetProperty<StreamAction>(x => x.Id)
            : ReflectionHelper.GetProperty<StreamAction>(x => x.Key!);

        var raw = Expression.Call(e, eMember.GetMethod!);
        Expression? wrapped = null;
        if (valueTypeInfo.Builder != null)
        {
            wrapped = Expression.Call(null, valueTypeInfo.Builder, raw);
        }
        else if (valueTypeInfo.Ctor != null)
        {
            wrapped = Expression.New(valueTypeInfo.Ctor, raw);
        }
        else
        {
            throw new NotSupportedException("Cannot build a type converter for strong typed id type " +
                                            valueTypeInfo.OuterType.FullNameInCode());
        }

        var lambda = Expression.Lambda<Func<StreamAction, TId>>(wrapped, e);

        return lambda.CompileFast();
    }

}