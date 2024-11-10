using System.Reflection;

namespace JasperFx.Core.Reflection;

public static class WalkReferencedAssemblies
{
    public static IEnumerable<Assembly> ForTypes(params Type[] types)
    {
        var stack = new Stack<Type>();

        foreach (var type in types)
        {
            stack.Push(type);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                yield return current.Assembly;

                if (!current.IsGenericType || current.IsGenericTypeDefinition)
                {
                    continue;
                }

                var typeArguments = current.GetGenericArguments();
                foreach (var typeArgument in typeArguments) stack.Push(typeArgument);
            }
        }
    }
}
