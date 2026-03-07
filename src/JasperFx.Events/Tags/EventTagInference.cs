using System.Collections.Concurrent;
using System.Reflection;

namespace JasperFx.Events.Tags;

/// <summary>
/// Provides runtime inference of tags from event data properties.
/// When an event is appended via IEventBoundary without explicit tags,
/// this utility scans the event type's public properties for any that match
/// registered tag types and creates EventTag values from them.
/// </summary>
public static class EventTagInference
{
    private static readonly ConcurrentDictionary<(Type EventType, Type TagType), Func<object, object>?> _propertyAccessors = new();

    /// <summary>
    /// Try to infer tags from the event data's public properties by matching
    /// property types against registered tag types. Returns the inferred tags,
    /// or an empty list if none could be inferred.
    /// </summary>
    public static List<EventTag> InferTags(object eventData, IReadOnlyList<TagTypeRegistration> registeredTagTypes)
    {
        var tags = new List<EventTag>();
        var eventType = eventData.GetType();

        foreach (var registration in registeredTagTypes)
        {
            var accessor = _propertyAccessors.GetOrAdd(
                (eventType, registration.TagType),
                static key => BuildAccessor(key.EventType, key.TagType));

            if (accessor == null) continue;

            var tagInstance = accessor(eventData);
            if (tagInstance == null) continue;

            var value = registration.ExtractValue(tagInstance);
            tags.Add(new EventTag(registration.TagType, value));
        }

        return tags;
    }

    private static Func<object, object>? BuildAccessor(Type eventType, Type tagType)
    {
        // Find a single public gettable property of the tag type on the event
        var matchingProps = eventType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == tagType && p.CanRead)
            .ToArray();

        if (matchingProps.Length != 1) return null;

        var prop = matchingProps[0];
        return obj => prop.GetValue(obj)!;
    }
}
