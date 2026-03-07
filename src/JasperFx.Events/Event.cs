using System.Collections.Concurrent;
using System.Linq.Expressions;
using FastExpressionCompiler;
using JasperFx.Core.Reflection;
using JasperFx.Events.Tags;

namespace JasperFx.Events;

#region sample_IEvent

#endregion

public interface IEvent<out T>: IEvent where T : notnull
{
    new T Data { get; }
}

public class Event<T>: IEvent<T> where T : notnull
{
    public Event(T data)
    {
        Data = data;
    }

    /// <summary>
    ///     The actual event data
    /// </summary>
    public T Data { get; set; }

    object IEvent.Data => Data;

    public Type EventType => typeof(T);
    public string EventTypeName { get; set; } = null!;
    public string DotNetTypeName { get; set; } = null!;

    public void SetHeader(string key, object value)
    {
        Headers ??= new Dictionary<string, object>();
        Headers[key] = value;
    }

    public object? GetHeader(string key)
    {
        return Headers?.TryGetValue(key, out var value) ?? false ? value : null;
    }

    public bool IsArchived { get; set; }

    public string? AggregateTypeName { get; set; }
    
    /// <summary>
    ///     Optional metadata describing the user name or
    ///     process name for this unit of work
    /// </summary>
    public string? UserName { get; set; }

    public bool IsSkipped { get; set; }

    protected bool Equals(Event<T> other)
    {
        return Id.Equals(other.Id);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((Event<T>)obj);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    #region sample_event_metadata

    /// <summary>
    ///     A reference to the stream that contains
    ///     this event
    /// </summary>
    public Guid StreamId { get; set; }

    /// <summary>
    ///     A reference to the stream if the stream
    ///     identifier mode is AsString
    /// </summary>
    public string? StreamKey { get; set; }

    /// <summary>
    ///     An alternative Guid identifier to identify
    ///     events across databases
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     An event's version position within its event stream
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    ///     A global sequential number identifying the Event
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    ///     The UTC time that this event was originally captured
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    public string TenantId { get; set; } = StorageConstants.DefaultTenantId;

    /// <summary>
    ///     Optional metadata describing the causation id
    /// </summary>
    public string? CausationId { get; set; }

    /// <summary>
    ///     Optional metadata describing the correlation id
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    ///     This is meant to be lazy created, and can be null
    /// </summary>
    public Dictionary<string, object>? Headers { get; set; }

    #endregion

    private List<EventTag>? _tags;

    /// <summary>
    /// Optional tags for DCB support. Lazy-created.
    /// </summary>
    public IReadOnlyList<EventTag>? Tags => _tags;

    /// <inheritdoc />
    public void AddTag<TTag>(TTag tag) where TTag : notnull
    {
        var value = TagValueExtractor.ExtractValue(tag);
        _tags ??= new List<EventTag>();
        _tags.Add(new EventTag(typeof(TTag), value));
    }

    /// <inheritdoc />
    public void AddTag(EventTag tag)
    {
        _tags ??= new List<EventTag>();
        _tags.Add(tag);
    }
}

/// <summary>
/// Provides memoized compiled lambdas for extracting the inner value from strong-typed identifiers.
/// Avoids reflection at runtime.
/// </summary>
internal static class TagValueExtractor
{
    private static readonly ConcurrentDictionary<Type, Func<object, object>> _unwrappers = new();

    public static object ExtractValue<TTag>(TTag tag) where TTag : notnull
    {
        return ExtractValue(typeof(TTag), tag);
    }

    public static object ExtractValue(Type tagType, object tag)
    {
        var unwrapper = _unwrappers.GetOrAdd(tagType, static type =>
        {
            // For primitive types, just box directly
            if (type == typeof(string) || type == typeof(Guid) || type == typeof(int) || type == typeof(long))
            {
                return static obj => obj;
            }

            var valueTypeInfo = ValueTypeInfo.ForType(type);
            var param = Expression.Parameter(typeof(object), "tag");
            var cast = Expression.Convert(param, valueTypeInfo.OuterType);
            var getValue = Expression.Property(cast, valueTypeInfo.ValueProperty);
            var box = Expression.Convert(getValue, typeof(object));
            var lambda = Expression.Lambda<Func<object, object>>(box, param);
            return lambda.CompileFast();
        });

        return unwrapper(tag);
    }
}