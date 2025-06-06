namespace JasperFx.Events.Grouping;

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
            var range = fanOutFunc(source).Select(x => source.CloneEventWithNewData(x)).ToArray();

            events.InsertRange(index + 1, range);

            starting = index + range.Length;
        }
    }


}
