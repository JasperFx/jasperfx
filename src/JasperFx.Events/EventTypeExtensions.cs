using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

/// <summary>
///     Class <c>EventMappingExtensions</c> exposes extensions and helpers to handle event type mapping.
/// </summary>
public static class EventTypeExtensions
{
    /// <summary>
    /// Get the event type name based on a style
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="style"></param>
    /// <returns></returns>
    public static string GetEventTypeName(this Type eventType, EventNamingStyle style)
    {
        switch (style)
        {
            case EventNamingStyle.ClassicTypeName:
                return eventType.GetEventTypeName();
            case EventNamingStyle.SmarterTypeName:
                return eventType.GetSmarterEventTypeName();
            default:
                return eventType.FullNameInCode();
        }
    }
    
    /// <summary>
    ///     Translates by convention the CLR type name into string event type name.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <param name="eventType">CLR event type</param>
    /// <returns>Mapped string event type name</returns>
    public static string GetEventTypeName(this Type eventType)
    {
        return eventType.IsGenericType ? eventType.ShortNameInCode() : eventType.Name.ToTableAlias();
    }

    public static string GetSmarterEventTypeName(this Type eventType)
    {
        if (eventType.IsNested)
        {
            var outer = eventType.DeclaringType.GetEventTypeName();
            var inner = eventType.IsGenericType ? eventType.ShortNameInCode() : eventType.Name.ToTableAlias();
            return $"{outer}.{inner}";
        }
        
        return eventType.IsGenericType ? eventType.ShortNameInCode() : eventType.Name.ToTableAlias();
    }

    /// <summary>
    ///     Translates by convention the CLR type name into string event type name.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <typeparam name="TEvent">CLR event type</typeparam>
    /// <returns>Mapped string event type name</returns>
    public static string GetEventTypeName<TEvent>()
    {
        return GetEventTypeName(typeof(TEvent));
    }

    /// <summary>
    ///     Translates by convention the event type name into string event type name and suffix.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <param name="eventTypeName">event type name</param>
    /// <param name="suffix">Type name suffix</param>
    /// <returns>Mapped string event type name in the format: $"{eventTypeName}_{suffix}"</returns>
    public static string GetEventTypeNameWithSuffix(this string eventTypeName, string suffix)
    {
        return $"{eventTypeName}_{suffix}";
    }

    /// <summary>
    ///     Translates by convention the CLR type name into string event type name and suffix.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <param name="eventType">CLR event type</param>
    /// <returns>Mapped string event type name with suffix</returns>
    public static string GetEventTypeNameWithSuffix(this Type eventType, string suffix)
    {
        return GetEventTypeNameWithSuffix(GetEventTypeName(eventType), suffix);
    }

    /// <summary>
    ///     Translates by convention the CLR type name into string event type name and suffix.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <typeparam name="TEvent">CLR event type</typeparam>
    /// <returns>Mapped string event type name with suffix</returns>
    public static string GetEventTypeNameWithSuffix<TEvent>(string suffix)
    {
        return GetEventTypeNameWithSuffix(typeof(TEvent), suffix);
    }

    /// <summary>
    ///     Translates by convention the CLR type name into string event type name with schema version suffix.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <param name="eventType">CLR event type</param>
    /// <param name="schemaVersion">Event schema version</param>
    /// <returns>Mapped string event type name with schema version suffix</returns>
    public static string GetEventTypeNameWithSchemaVersion(Type eventType, uint schemaVersion)
    {
        return GetEventTypeNameWithSuffix(eventType, $"v{schemaVersion}");
    }

    /// <summary>
    ///     Translates by convention the CLR type name into string event type name with schema version suffix.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <typeparam name="TEvent">CLR event type</typeparam>
    /// <param name="schemaVersion">Event schema version</param>
    /// <returns>Mapped string event type name with schema version suffix</returns>
    public static string GetEventTypeNameWithSchemaVersion<TEvent>(uint schemaVersion)
    {
        return GetEventTypeNameWithSchemaVersion(typeof(TEvent), schemaVersion);
    }

    /// <summary>
    ///     Translates by convention the event type name into string event type name with schema version suffix.
    ///     It can handle both regular and generic types.
    /// </summary>
    /// <param name="eventTypeName">event type name</param>
    /// <param name="schemaVersion">Event schema version</param>
    /// <returns>Mapped string event type name in the format: $"{eventTypeName}_{version}"</returns>
    public static string GetEventTypeNameWithSchemaVersion(string eventTypeName, uint schemaVersion)
    {
        return GetEventTypeNameWithSuffix(eventTypeName, $"v{schemaVersion}");
    }
    
    
    /// <summary>
    /// Create a new IEvent object carrying the original metadata as the original
    /// event, but with a different data body. This is used within "fan out"
    /// operations within event slicing for multi-stream projections
    /// </summary>
    /// <param name="event"></param>
    /// <param name="eventData"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IEvent<T> CloneEventWithNewData<T>(this IEvent @event, T eventData) where T : notnull
    {
        return new Event<T>(eventData)
        {
            Id = @event.Id,
            Sequence = @event.Sequence,
            TenantId = @event.TenantId,
            Version = @event.Version,
            StreamId = @event.StreamId,
            StreamKey = @event.StreamKey,
            Timestamp = @event.Timestamp
        };
    }

}
