using ImTools;

namespace JasperFx.Events;

public static class EventIdentity
{
    private static ImHashMap<Type, object> _functions = ImHashMap<Type, object>.Empty;
    
    /// <summary>
    /// Use to get an identity off of an IEvent for single stream aggregations
    /// </summary>
    /// <param name="e"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T IdentityFor<T>(IEvent e) where T : notnull
    {
        if (_functions.TryFind(typeof(T), out var raw))
        {
            if (raw is Func<IEvent, T> func) return func(e);
        }

        var identitySource = IEvent.CreateAggregateIdentitySource<T>();
        _functions = _functions.AddOrUpdate(typeof(T), identitySource);

        return identitySource(e);
    }
}