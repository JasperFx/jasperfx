using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

public static class EventExtensions
{
    public static Type? UnwrapEventType(this Type type)
    {
        if (type.Closes(typeof(IEvent<>))) return type.GetGenericArguments()[0];
        if (type.Closes(typeof(Event<>))) return type.GetGenericArguments()[0];

        if (type == typeof(IEvent)) return null;

        return type;
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
    
    public static Type GetEventType(this MethodInfo method, Type aggregateType)
    {
        var candidate = method.GetParameters().Where(x => x.ParameterType.Closes(typeof(IEvent<>)));
        if (candidate.Count() == 1)
        {
            return candidate.Single().ParameterType.GetGenericArguments()[0];
        }

        var parameterInfo = method.GetParameters().FirstOrDefault(x => x.Name == "@event" || x.Name == "event");
        if (parameterInfo == null)
        {
            var candidates = method
                .GetParameters()
                .Where(x => !x.ParameterType.Assembly.HasAttribute<JasperFxAssemblyAttribute>())
                .Where(x => x.ParameterType.Assembly != typeof(IEvent).Assembly)
                .Where(x => x.ParameterType != aggregateType).ToList();

            if (candidates.Count == 1)
            {
                parameterInfo = candidates.Single();
            }
            else
            {
                return null;
            }
        }

        if (parameterInfo.ParameterType.Closes(typeof(Event<>)))
        {
            return parameterInfo.ParameterType.GetGenericArguments()[0];
        }


        return parameterInfo.ParameterType;
    }
}