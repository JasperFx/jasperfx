using System.Reflection;
using System.Text;

namespace JasperFx.Core.Reflection;

public static class ReflectionExtensions
{
    /// <summary>
    /// Tries to find a custom attribute, and finds the first of the type
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="att"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static bool TryGetAttribute<T>(this ICustomAttributeProvider provider, out T att) where T : Attribute
    {
        att = provider.GetCustomAttributes(typeof(T), true).OfType<T>().FirstOrDefault();

        return att != null;
    }
    
    public static T? GetAttribute<T>(this MemberInfo provider) where T : Attribute
    {
        var atts = provider.GetCustomAttributes(typeof(T), true);
        return atts.FirstOrDefault() as T;
    }

    public static T? GetAttribute<T>(this Assembly provider) where T : Attribute
    {
        var atts = provider.GetCustomAttributes(typeof(T));
        return atts.FirstOrDefault() as T;
    }

    public static T? GetAttribute<T>(this Module provider) where T : Attribute
    {
        var atts = provider.GetCustomAttributes(typeof(T));
        return atts.FirstOrDefault() as T;
    }

    public static T? GetAttribute<T>(this ParameterInfo provider) where T : Attribute
    {
        var atts = provider.GetCustomAttributes(typeof(T), true);
        return atts.FirstOrDefault() as T;
    }

    public static IEnumerable<T> GetAllAttributes<T>(this Assembly provider) where T : Attribute
    {
        return provider.GetCustomAttributes(typeof(T)).OfType<T>();
    }

    public static IEnumerable<T> GetAllAttributes<T>(this MemberInfo provider) where T : Attribute
    {
        return provider.GetCustomAttributes(typeof(T), true).OfType<T>();
    }

    public static IEnumerable<T> GetAllAttributes<T>(this Module provider) where T : Attribute
    {
        return provider.GetCustomAttributes(typeof(T)).OfType<T>();
    }

    public static IEnumerable<T> GetAllAttributes<T>(this ParameterInfo provider) where T : Attribute
    {
        return provider.GetCustomAttributes(typeof(T), true).OfType<T>();
    }

    public static bool HasAttribute<T>(this Assembly provider) where T : Attribute
    {
        return provider.IsDefined(typeof(T));
    }

    public static bool HasAttribute<T>(this MemberInfo provider) where T : Attribute
    {
        return provider.IsDefined(typeof(T), true);
    }

    public static bool HasAttribute<T>(this Module provider) where T : Attribute
    {
        return provider.IsDefined(typeof(T));
    }

    public static bool HasAttribute<T>(this ParameterInfo provider) where T : Attribute
    {
        return provider.IsDefined(typeof(T), true);
    }

    public static void ForAttribute<T>(this Assembly provider, Action<T> action) where T : Attribute
    {
        foreach (var attribute in provider.GetAllAttributes<T>()) action(attribute);
    }

    public static void ForAttribute<T>(this MemberInfo provider, Action<T> action) where T : Attribute
    {
        foreach (var attribute in provider.GetAllAttributes<T>()) action(attribute);
    }

    public static void ForAttribute<T>(this Module provider, Action<T> action) where T : Attribute
    {
        foreach (var attribute in provider.GetAllAttributes<T>()) action(attribute);
    }

    public static void ForAttribute<T>(this ParameterInfo provider, Action<T> action) where T : Attribute
    {
        foreach (var attribute in provider.GetAllAttributes<T>()) action(attribute);
    }

    public static void ForAttribute<T>(this Assembly provider, Action<T> action, Action elseDo)
        where T : Attribute
    {
        var found = false;
        foreach (var attribute in provider.GetAllAttributes<T>())
        {
            action(attribute);
            found = true;
        }

        if (!found)
        {
            elseDo();
        }
    }

    public static void ForAttribute<T>(this MemberInfo provider, Action<T> action, Action elseDo)
        where T : Attribute
    {
        var found = false;
        foreach (var attribute in provider.GetAllAttributes<T>())
        {
            action(attribute);
            found = true;
        }

        if (!found)
        {
            elseDo();
        }
    }

    public static void ForAttribute<T>(this Module provider, Action<T> action, Action elseDo)
        where T : Attribute
    {
        var found = false;
        foreach (var attribute in provider.GetAllAttributes<T>())
        {
            action(attribute);
            found = true;
        }

        if (!found)
        {
            elseDo();
        }
    }

    public static void ForAttribute<T>(this ParameterInfo provider, Action<T> action, Action elseDo)
        where T : Attribute
    {
        var found = false;
        foreach (var attribute in provider.GetAllAttributes<T>())
        {
            action(attribute);
            found = true;
        }

        if (!found)
        {
            elseDo();
        }
    }


    /// <summary>
    ///     Does a .Net type have a default, no arg constructor
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    public static bool HasDefaultConstructor(this Type t)
    {
        return t.IsValueType || t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null) != null;
    }

    /// <summary>
    /// Does this type have any constructors with arguments?
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    public static bool HasConstructorsWithArguments(this Type t)
    {
        return t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(x => x.GetParameters().Any());
    }

    // http://stackoverflow.com/a/15273117/426840
    /// <summary>
    ///     Is the object an anonymous type that is not within a .Net
    ///     namespace. See http://stackoverflow.com/a/15273117/426840
    /// </summary>
    /// <param name="instance"></param>
    /// <returns></returns>
    public static bool IsAnonymousType(this object? instance)
    {
        if (instance == null)
        {
            return false;
        }

        return instance.GetType().Namespace == null;
    }

    /// <summary>
    ///     Get a user readable, "pretty" type name for a given type. Corrects for
    ///     generics and inner classes
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    public static string GetPrettyName(this Type t)
    {
        if (!t.GetTypeInfo().IsGenericType)
        {
            return t.Name;
        }

        var sb = new StringBuilder();

        sb.Append(t.Name.Substring(0, t.Name.LastIndexOf("`", StringComparison.Ordinal)));
        sb.Append(t.GetGenericArguments().Aggregate("<",
            (aggregate, type) => aggregate + (aggregate == "<" ? "" : ",") + GetPrettyName(type)));
        sb.Append('>');

        return sb.ToString();
    }
    
    /// <summary>
    /// Is a method asynchronous?
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    public static bool IsAsync(this MethodInfo method)
    {
        if (method.ReturnType == null)
        {
            return false;
        }

        if (method.ReturnType == typeof(ValueTask) || method.ReturnType.Closes(typeof(ValueTask<>)))
        {
            return true;
        }

        return method.ReturnType == typeof(Task) || method.ReturnType.Closes(typeof(Task<>));
    }
    
    public static bool CanBeOverridden(this MethodInfo method)
    {
        if (method.IsAbstract)
        {
            return true;
        }

        if (method.IsVirtual && !method.IsFinal)
        {
            return true;
        }

        return false;
    }
    
}