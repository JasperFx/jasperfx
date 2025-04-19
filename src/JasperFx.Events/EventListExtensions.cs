namespace JasperFx.Events;

public static class EventListExtensions
{
    public static void FanOut<TSource, TChild>(this List<IEvent> events,
        Func<TSource, IEnumerable<TChild>> fanOutFunc) where TSource : notnull where TChild : notnull
    {
        FanOut<TSource, TChild>(events, source => fanOutFunc(source.Data));
    }

    public static void FanOut<TSource, TChild>(this List<IEvent> events, Func<IEvent<TSource>, IEnumerable<TChild>> fanOutFunc) where TSource : notnull where TChild : notnull
    {
        var matches = events.OfType<Event<TSource>>().ToArray();
        var starting = 0;
        foreach (var source in matches)
        {
            var index = events.IndexOf(source, starting);
            var range = fanOutFunc(source).Select(x => source.WithData(x)).ToArray();

            events.InsertRange(index + 1, range);

            starting = index + range.Length;
        }
    }

    public static bool HasAnyEventsOfType<T>(this IEnumerable<IEvent> events) where T : notnull
    {
        return events.OfType<IEvent<T>>().Any();
    }

    public static bool HasNoEventsOfType<T>(this IEnumerable<IEvent> events) where T : notnull
    {
        return !events.HasAnyEventsOfType<T>();
    }
    
    public static bool HasAnyElementsOfType<T>(this IEnumerable<object> data)
    {
        return data.OfType<T>().Any();
    }

    public static bool HasNoElementsOfType<T>(this IEnumerable<object> data)
    {
        return !data.HasAnyElementsOfType<T>();
    }

    public static Queue<object> ToQueueOfEventData(this IEnumerable<IEvent> events)
    {
        return new Queue<object>(events.Select(x => x.Data));
    }
    
    
}
