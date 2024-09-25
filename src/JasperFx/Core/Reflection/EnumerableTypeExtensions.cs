namespace JasperFx.Core.Reflection;

public static class EnumerableTypeExtensions
{
    /// <summary>
    /// Is the type an enumerable of some sort? Includes arrays
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static bool IsEnumerable(this Type type)
    {
        if (type.IsArray) return true;

        return type.IsGenericType && _enumerableTypes.Contains(type.GetGenericTypeDefinition());
    }

    /// <summary>
    /// Tells you the element type of various forms of enumerables or arrays
    /// </summary>
    /// <param name="serviceType"></param>
    /// <returns></returns>
    public static Type? DetermineElementType(this Type serviceType)
    {
        if (serviceType.IsArray)
        {
            return serviceType.GetElementType();
        }

        return serviceType.GetGenericArguments().First();
    }
        
    private static readonly List<Type> _enumerableTypes = new List<Type>
    {
        typeof (IEnumerable<>),
        typeof (IList<>),
        typeof (IReadOnlyList<>),
        typeof (List<>),
        typeof (ICollection<>),
        typeof (IReadOnlyCollection<>)
    };
}