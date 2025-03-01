using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections;

public static class CodeGenerationExtensions
{
    public static Type? UnwrapEventType(this Type type)
    {
        if (type.Closes(typeof(IEvent<>))) return type.GetGenericArguments()[0];
        if (type.Closes(typeof(Event<>))) return type.GetGenericArguments()[0];

        if (type == typeof(IEvent)) return null;

        return type;
    }
    
    public static Type GetEventType(this MethodInfo method, Type aggregateType)
    {
        var candidate = method.GetParameters().Where(x => x.ParameterType.Closes(typeof(IEvent<>)));
        if (candidate.Count() == 1)
        {
            return candidate.Single().ParameterType.GetGenericArguments()[0];
        }

        if (aggregateType == null)
        {
            var parameters = method.GetParameters().Where(x => x.ParameterType != typeof(IEvent) && x.ParameterType.IsConcrete());
            if (parameters.Count() == 1)
            {
                return parameters.Single().ParameterType;
            }
        }
        else
        {
            var parameters = method.GetParameters().Where(x => x.ParameterType != typeof(IEvent) && x.ParameterType.IsConcrete() && x.ParameterType != aggregateType);
            if (parameters.Count() == 1)
            {
                return parameters.Single().ParameterType;
            }
        }

        var parameterInfo = method.GetParameters().FirstOrDefault(x => x.Name == "@event" || x.Name == "event");
        if (parameterInfo == null)
        {
            var candidates = method
                .GetParameters()
                .Where(x => !x.ParameterType.Assembly.HasAttribute<JasperFxAssemblyAttribute>())
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