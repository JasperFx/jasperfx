using System.Reflection;
using System.Text.RegularExpressions;

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
        if (Aliases.TryGetValue(type, out var code))
        {
            return code;
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
                return $"{type.ReflectedType!.FullNameInCode()}.{cleanName}<{args}>";
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
    ///     F# keyword aliases for the primitive .NET types, used by <see cref="FSharpName" />.
    ///     Note the deliberate differences from C#: <c>System.Double</c> is <c>float</c> in F#
    ///     (and <c>System.Single</c> is <c>float32</c>), and <c>System.Void</c> is <c>unit</c>.
    /// </summary>
    public static readonly Dictionary<Type, string> FSharpAliases = new()
    {
        { typeof(void), "unit" },
        { typeof(object), "obj" },
        { typeof(string), "string" },
        { typeof(bool), "bool" },
        { typeof(char), "char" },
        { typeof(byte), "byte" },
        { typeof(sbyte), "sbyte" },
        { typeof(short), "int16" },
        { typeof(ushort), "uint16" },
        { typeof(int), "int" },
        { typeof(uint), "uint32" },
        { typeof(long), "int64" },
        { typeof(ulong), "uint64" },
        { typeof(float), "float32" },
        { typeof(double), "float" },
        { typeof(decimal), "decimal" },
        { typeof(nint), "nativeint" },
        { typeof(nuint), "unativeint" }
    };

    /// <summary>
    ///     EXPERIMENTAL. Derives the full type name *as it would appear in F# code*. Handles the
    ///     primitive aliases (see <see cref="FSharpAliases" />), arrays, and closed generic types.
    ///     Throws <see cref="NotSupportedException" /> on cases the F# code generator does not yet
    ///     handle (open generics, generic parameters, by-ref/pointer types, and tuple types) so
    ///     unsupported shapes fail loudly rather than emitting invalid F#.
    /// </summary>
    public static string FSharpName(this Type type)
    {
        if (FSharpAliases.TryGetValue(type, out var alias))
        {
            return alias;
        }

        if (type.IsByRef || type.IsPointer || type.IsGenericParameter)
        {
            throw new NotSupportedException(
                $"F# code generation does not yet support the type '{type}'.");
        }

        if (type.IsArray)
        {
            return $"{type.GetElementType()!.FSharpName()}[]";
        }

        if (type.IsGenericType)
        {
            if (type.IsGenericTypeDefinition)
            {
                throw new NotSupportedException(
                    $"F# code generation does not support the open generic type '{type}'.");
            }

            var definition = type.GetGenericTypeDefinition();
            if (definition.FullName != null &&
                (definition.FullName.StartsWith("System.ValueTuple") ||
                 definition.FullName.StartsWith("System.Tuple")))
            {
                throw new NotSupportedException(
                    $"F# code generation does not yet support the tuple type '{type}'.");
            }

            var cleanName = type.Name.Split('`').First();
            var args = type.GetGenericArguments().Select(x => x.FSharpName()).Join(", ");

            if (type.IsNested)
            {
                return $"{type.ReflectedType!.FSharpName()}.{cleanName}<{args}>";
            }

            return $"{type.Namespace}.{cleanName}<{args}>";
        }

        // Non-generic, non-primitive: the fully-qualified C# name is also valid F#.
        return type.FullNameInCode();
    }

    /// <summary>
    ///     Derives the type name *as it would appear in C# code*
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static string NameInCode(this Type type)
    {
        if (Aliases.TryGetValue(type, out var code))
        {
            return code;
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
        if (Aliases.TryGetValue(type, out var code))
        {
            return code;
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
    
    public static string Sanitize(this string value)
    {
        return Regex.Replace(value, @"[\#\<\>\,\.\]\[\`\+\-]", "_").Replace(" ", "");
    }

    public static string ToTypeNamePart(this Type type)
    {
        if (type.IsGenericType)
        {
            return type.Name.Split('`').First() + "_of_" +
                   type.GetGenericArguments().Select(x => x.ToTypeNamePart()).Join("_");
        }

        return type.Name;
    }
}