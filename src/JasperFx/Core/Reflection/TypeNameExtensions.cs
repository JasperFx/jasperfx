using System.Reflection;

namespace JasperFx.Core.Reflection;

public static class TypeNameExtensions
{
    public static readonly Dictionary<Type, string> Aliases = new()
    {
        { typeof(int), "int" },
        { typeof(void), "void" },
        { typeof(string), "string" },
        { typeof(long), "long" },
        { typeof(double), "double" },
        { typeof(bool), "bool" },
        { typeof(object), "object" },
        { typeof(object[]), "object[]" }
    };

    /// <summary>
    ///     Derives the full type name *as it would appear in C# code*
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static string FullNameInCode(this Type type)
    {
        if (Aliases.ContainsKey(type))
        {
            return Aliases[type];
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var cleanName = type.Name.Split('`').First();
            if (type.IsNested && type.DeclaringType?.IsGenericTypeDefinition == true)
            {
                cleanName = $"{type.ReflectedType!.NameInCode(type.GetGenericArguments())}.{cleanName}";
                return $"{type.Namespace}.{cleanName}";
            }

            var args = type.GetGenericArguments().Select(x => x.FullNameInCode()).Join(", ");

            if (type.IsNested)
            {
                return $"{type.ReflectedType.FullNameInCode()}.{cleanName}<{args}>";
            }

            return $"{type.Namespace}.{cleanName}<{args}>";
        }

        if (type.IsOpenGeneric())
        {
            return type.Namespace + "." + type.NameInCode();
        }
        
        if (type.FullName == null)
        {
            return type.Name;
        }

        if (type.IsNested)
        {
            return $"{type.ReflectedType!.FullNameInCode()}.{type.Name}";
        }

        return type.FullName.Replace("+", ".");
    }

    /// <summary>
    ///     Derives the type name *as it would appear in C# code*
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static string NameInCode(this Type type)
    {
        if (Aliases.ContainsKey(type))
        {
            return Aliases[type];
        }

        if (type.IsGenericType)
        {
            if (type.IsGenericTypeDefinition)
            {
                var parts = type.Name.Split('`');
                ;
                var cleanName = parts.First().Replace("+", ".");

                var hasArgs = parts.Length > 1;
                if (hasArgs)
                {
                    var numberOfArgs = int.Parse(parts[1]) - 1;
                    cleanName = $"{cleanName}<{"".PadLeft(numberOfArgs, ',')}>";
                }

                if (type.IsNested)
                {
                    cleanName = $"{type.ReflectedType!.NameInCode()}.{cleanName}";
                }

                return cleanName;
            }
            else
            {
                var cleanName = type.Name.Split('`').First().Replace("+", ".");
                if (type.IsNested)
                {
                    cleanName = $"{type.ReflectedType!.NameInCode()}.{cleanName}";
                }

                var args = type.GetGenericArguments().Select(x => x.FullNameInCode()).Join(", ");

                return $"{cleanName}<{args}>";
            }
        }

        if (type.MemberType == MemberTypes.NestedType)
        {
            return $"{type.ReflectedType!.NameInCode()}.{type.Name}";
        }

        return type.Name.Replace("+", ".").Replace("`", "_");
    }

    /// <summary>
    ///     Derives the type name *as it would appear in C# code* for a type with generic parameters
    /// </summary>
    /// <param name="type"></param>
    /// <param name="genericParameterTypes"></param>
    /// <returns></returns>
    public static string NameInCode(this Type type, Type[] genericParameterTypes)
    {
        var cleanName = type.Name.Split('`').First().Replace("+", ".");
        var args = genericParameterTypes.Select(x => x.FullNameInCode()).Join(", ");
        return $"{cleanName}<{args}>";
    }

    public static string ShortNameInCode(this Type type)
    {
        if (Aliases.ContainsKey(type))
        {
            return Aliases[type];
        }

        try
        {
            if (type.IsGenericType)
            {
                if (type.IsGenericTypeDefinition)
                {
                    var parts = type.Name.Split('`');

                    var cleanName = parts.First().Replace("+", ".");

                    var hasArgs = parts.Length > 1;
                    if (hasArgs)
                    {
                        var numberOfArgs = int.Parse(parts[1]) - 1;
                        cleanName = $"{cleanName}<{"".PadLeft(numberOfArgs, ',')}>";
                    }

                    if (type.IsNested)
                    {
                        cleanName = $"{type.ReflectedType!.NameInCode()}.{cleanName}";
                    }

                    return cleanName;
                }
                else
                {
                    var cleanName = type.Name.Split('`').First().Replace("+", ".");
                    if (type.IsNested)
                    {
                        cleanName = $"{type.ReflectedType!.NameInCode()}.{cleanName}";
                    }

                    var args = type.GetGenericArguments().Select(x => x.ShortNameInCode()).Join(", ");

                    return $"{cleanName}<{args}>";
                }
            }

            if (type.MemberType == MemberTypes.NestedType)
            {
                return $"{type.ReflectedType!.NameInCode()}.{type.Name}";
            }

            return type.Name.Replace("+", ".");
        }
        catch (Exception)
        {
            return type.Name;
        }
    }

    /// <summary>
    ///     Creates a deterministic class name for the supplied type
    ///     and suffix. Uses a hash of the type's full name to disambiguate
    ///     between derivations on the same original type name
    /// </summary>
    /// <param name="type"></param>
    /// <param name="suffix"></param>
    /// <returns></returns>
    public static string ToSuffixedTypeName(this Type type, string suffix)
    {
        var prefix = type.Name.Split('`').First();
        var hash = Math.Abs(type.FullNameInCode().GetStableHashCode());
        return $"{prefix}{suffix}{hash}".Replace("-", "");
    }
}