using System.Linq.Expressions;
using FastExpressionCompiler;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

/// <summary>
/// Utility to build event wrappers for JasperFx event stores
/// </summary>
public static class Event
{
    /// <summary>
    /// Convenience method to wrap an object with the JasperFx Event typed wrapper
    /// </summary>
    /// <param name="data"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IEvent<T> For<T>(T data) where T : notnull => new Event<T>(data);

    /// <summary>
    /// Wrap an object with the correct JasperFx event envelope
    /// </summary>
    /// <param name="data"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IEvent<T> AsEvent<T>(this T data) where T : notnull => For(data);

    /// <summary>
    /// Set the timestamp of an event
    /// </summary>
    /// <param name="e"></param>
    /// <param name="timestamp"></param>
    /// <returns></returns>
    public static IEvent AtTimestamp(this IEvent e, DateTimeOffset timestamp)
    {
        e.Timestamp = timestamp;
        return e;
    }

    /// <summary>
    /// Add an event to just this event
    /// </summary>
    /// <param name="e"></param>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public static IEvent WithHeader(this IEvent e, string key, object value)
    {
        e.SetHeader(key, value);
        return e;
    }
    
    // More???????
}

public interface IEvent
{
    /// <summary>
    ///     Unique identifier for the event. Uses a sequential Guid
    /// </summary>
    Guid Id { get; set; }

    /// <summary>
    ///     The version of the stream this event reflects. The place in the stream.
    /// </summary>
    long Version { get; set; }

    /// <summary>
    ///     The sequential order of this event in the entire event store
    /// </summary>
    long Sequence { get; set; }

    /// <summary>
    ///     The actual event data body
    /// </summary>
    object Data { get; }

    /// <summary>
    ///     If using Guid's for the stream identity, this will
    ///     refer to the Stream's Id, otherwise it will always be Guid.Empty
    /// </summary>
    Guid StreamId { get; set; }

    /// <summary>
    ///     If using strings as the stream identifier, this will refer
    ///     to the containing Stream's Id
    /// </summary>
    string? StreamKey { get; set; }

    /// <summary>
    ///     The UTC time that this event was originally captured
    /// </summary>
    DateTimeOffset Timestamp { get; set; }

    /// <summary>
    ///     If using multi-tenancy by tenant id
    /// </summary>
    string TenantId { get; set; }

    /// <summary>
    ///     The .Net type of the event body
    /// </summary>
    Type EventType { get; }

    /// <summary>
    ///     JasperFx.Event's type alias string for the Event type
    /// </summary>
    string EventTypeName { get; set; }

    /// <summary>
    ///     JasperFx.Events's string representation of the event type
    ///     in assembly qualified name
    /// </summary>
    string DotNetTypeName { get; set; }

    /// <summary>
    ///     Optional metadata describing the causation id
    /// </summary>
    string? CausationId { get; set; }

    /// <summary>
    ///     Optional metadata describing the correlation id
    /// </summary>
    string? CorrelationId { get; set; }

    /// <summary>
    ///     Optional user defined metadata values. This may be null.
    /// </summary>
    Dictionary<string, object>? Headers { get; set; }

    /// <summary>
    ///     Has this event been archived and no longer applicable
    ///     to projected views
    /// </summary>
    bool IsArchived { get; set; }

    /// <summary>
    ///     JasperFx.Events's name for the aggregate type that will be persisted
    ///     to the streams table. This will only be available when running
    ///     within the Async Daemon
    /// </summary>
    public string? AggregateTypeName { get; set; }

    /// <summary>
    ///     Set an optional user defined metadata value by key
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    void SetHeader(string key, object value);

    /// <summary>
    ///     Get an optional user defined metadata value by key
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    object? GetHeader(string key);
    
    /// <summary>
    /// Build a Func that can resolve an identity from the IEvent and even
    /// handles the dastardly strong typed identifiers
    /// </summary>
    /// <typeparam name="TId"></typeparam>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public static Func<IEvent, TId> CreateAggregateIdentitySource<TId>()
        where TId : notnull
    {
        if (typeof(TId) == typeof(Guid)) return e => e.StreamId.As<TId>();
        if (typeof(TId) == typeof(string)) return e => e.StreamKey!.As<TId>();
        
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(TId));
        
        var e = Expression.Parameter(typeof(IEvent), "e");
        var eMember = valueTypeInfo.SimpleType == typeof(Guid)
            ? ReflectionHelper.GetProperty<IEvent>(x => x.StreamId)
            : ReflectionHelper.GetProperty<IEvent>(x => x.StreamKey!);

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

        var lambda = Expression.Lambda<Func<IEvent, TId>>(wrapped, e);

        return lambda.CompileFast();
    }
}