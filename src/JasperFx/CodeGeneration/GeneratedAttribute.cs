using System.Globalization;
using System.Reflection;
using System.Text;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration;

/// <summary>
///     A single argument passed to a <see cref="GeneratedAttribute" />. Arguments are a small,
///     closed model rather than raw text so that every language writer can render them in its own
///     syntax — <c>typeof(T)</c> in C#, <c>typeof&lt;T&gt;</c> in F#, <c>|</c> vs <c>|||</c> for
///     combined enum flags, and so on.
/// </summary>
/// <remarks>
///     Rendering is always fully qualified (and <c>global::</c>-prefixed in C#), so an attribute
///     never depends on the <c>using</c> / <c>open</c> statements at the top of the generated file.
/// </remarks>
public abstract class AttributeArg
{
    private const string Suffix = "Attribute";

    /// <summary>
    ///     A <c>typeof()</c> argument for a type that exists at code generation time.
    /// </summary>
    public static AttributeArg Type(System.Type type)
    {
        return new TypeArg(type ?? throw new ArgumentNullException(nameof(type)));
    }

    /// <summary>
    ///     A <c>typeof()</c> argument naming a type by its (fully qualified) name in code rather
    ///     than by a runtime <see cref="System.Type" />. This is the form to use for a *sibling
    ///     generated type* — a type that only exists in the file being emitted and therefore has
    ///     no runtime <see cref="System.Type" /> while <c>codegen write</c> is running.
    /// </summary>
    /// <param name="typeName">
    ///     The type name exactly as it should appear inside <c>typeof(...)</c>. Fully qualify it;
    ///     no <c>using</c> / <c>open</c> bookkeeping is done on your behalf.
    /// </param>
    public static AttributeArg TypeNamed(string typeName)
    {
        if (typeName.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(typeName), "The type name cannot be empty");
        }

        return new TypeNamedArg(typeName);
    }

    /// <summary>
    ///     An enum member argument, e.g. <c>DynamicallyAccessedMemberTypes.All</c>. Combined
    ///     <c>[Flags]</c> values are rendered with the language's bitwise-or operator.
    /// </summary>
    public static AttributeArg Enum(System.Enum value)
    {
        return new EnumArg(value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    ///     A literal argument — string, bool, char, numeric, enum, <see cref="System.Type" />, or
    ///     <c>null</c>.
    /// </summary>
    public static AttributeArg Value(object? value)
    {
        return value switch
        {
            AttributeArg arg => arg,
            System.Type type => new TypeArg(type),
            System.Enum e => new EnumArg(e),
            _ => new ValueArg(value)
        };
    }

    /// <summary>
    ///     An escape hatch for anything the model above cannot express (named arguments, arrays,
    ///     <c>nameof</c>, …). The text is emitted verbatim.
    /// </summary>
    /// <param name="code">The C# text.</param>
    /// <param name="fsharpCode">The F# text. Defaults to <paramref name="code" />.</param>
    public static AttributeArg Raw(string code, string? fsharpCode = null)
    {
        return new RawArg(code, fsharpCode);
    }

    /// <summary>
    ///     Render this argument as it should appear inside a C# attribute usage.
    /// </summary>
    public abstract string ToCSharp();

    /// <summary>
    ///     Render this argument as it should appear inside an F# attribute usage.
    /// </summary>
    public abstract string ToFSharp();

    /// <summary>
    ///     Assemblies that must be referenced for the emitted argument to compile. Only matters on
    ///     the runtime Roslyn path; the <c>codegen write</c> path never compiles in-process.
    /// </summary>
    public virtual IEnumerable<Assembly> AssemblyReferences()
    {
        yield break;
    }

    public override string ToString()
    {
        return ToCSharp();
    }

    /// <summary>
    ///     Prefix a name with <c>global::</c> when it is namespace-qualified. Aliased primitives
    ///     (<c>int</c>, <c>string</c>, …) and already-prefixed names are left alone, because
    ///     <c>global::int</c> does not compile.
    /// </summary>
    internal static string QualifyForCSharp(string name)
    {
        if (!name.Contains('.') || name.StartsWith("global::", StringComparison.Ordinal))
        {
            return name;
        }

        return "global::" + name;
    }

    /// <summary>
    ///     Strip the conventional <c>Attribute</c> suffix from an attribute type's name in code,
    ///     matching how attributes are written by hand. <c>System.Attribute</c> itself and any
    ///     type whose simple name *is* "Attribute" are left alone.
    /// </summary>
    internal static string TrimAttributeSuffix(string nameInCode)
    {
        if (nameInCode.Length <= Suffix.Length || !nameInCode.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return nameInCode;
        }

        // Guard against "System.Attribute" collapsing to "System."
        if (nameInCode[nameInCode.Length - Suffix.Length - 1] == '.')
        {
            return nameInCode;
        }

        return nameInCode.Substring(0, nameInCode.Length - Suffix.Length);
    }

    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private sealed class TypeArg : AttributeArg
    {
        private readonly System.Type _type;

        public TypeArg(System.Type type)
        {
            _type = type;
        }

        public override string ToCSharp()
        {
            return $"typeof({QualifyForCSharp(_type.FullNameInCode())})";
        }

        public override string ToFSharp()
        {
            return $"typeof<{_type.FSharpName()}>";
        }

        public override IEnumerable<Assembly> AssemblyReferences()
        {
            yield return _type.Assembly;
        }
    }

    private sealed class TypeNamedArg : AttributeArg
    {
        private readonly string _typeName;

        public TypeNamedArg(string typeName)
        {
            _typeName = typeName;
        }

        public override string ToCSharp()
        {
            return $"typeof({QualifyForCSharp(_typeName)})";
        }

        public override string ToFSharp()
        {
            return $"typeof<{_typeName}>";
        }
    }

    private sealed class EnumArg : AttributeArg
    {
        private readonly System.Enum _value;

        public EnumArg(System.Enum value)
        {
            _value = value;
        }

        public override string ToCSharp()
        {
            return render(QualifyForCSharp(_value.GetType().FullNameInCode()), " | ");
        }

        public override string ToFSharp()
        {
            return render(_value.GetType().FSharpName(), " ||| ");
        }

        public override IEnumerable<Assembly> AssemblyReferences()
        {
            yield return _value.GetType().Assembly;
        }

        private string render(string typeName, string separator)
        {
            // A combined [Flags] value with no single matching member renders as "A, B". Each
            // member has to be qualified separately and joined with the language's bitwise-or.
            var members = _value.ToString().Split(',').Select(x => x.Trim()).Where(x => x.IsNotEmpty()).ToArray();

            if (members.Length == 0 || members.Any(x => !char.IsLetter(x[0]) && x[0] != '_'))
            {
                // Not a named member (an undefined numeric value) — cast the raw number instead.
                var numeric = Convert.ToInt64(_value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
                return $"({typeName}){numeric}";
            }

            return members.Select(x => $"{typeName}.{x}").Join(separator);
        }
    }

    private sealed class ValueArg : AttributeArg
    {
        private readonly object? _value;

        public ValueArg(object? value)
        {
            _value = value;
        }

        public override string ToCSharp()
        {
            return _value switch
            {
                null => "null",
                string s => Quote(s),
                bool b => b ? "true" : "false",
                char c => $"'{c}'",
                float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
                double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
                decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
                long l => l.ToString(CultureInfo.InvariantCulture) + "L",
                ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
                uint ui => ui.ToString(CultureInfo.InvariantCulture) + "u",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => _value.ToString() ?? "null"
            };
        }

        public override string ToFSharp()
        {
            return _value switch
            {
                null => "null",
                string s => Quote(s),
                bool b => b ? "true" : "false",
                char c => $"'{c}'",
                float f => withDecimalPoint(f.ToString("R", CultureInfo.InvariantCulture)) + "f",
                double d => withDecimalPoint(d.ToString("R", CultureInfo.InvariantCulture)),
                decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
                long l => l.ToString(CultureInfo.InvariantCulture) + "L",
                ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
                uint ui => ui.ToString(CultureInfo.InvariantCulture) + "u",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => _value.ToString() ?? "null"
            };
        }

        // F# has no "d" suffix; a floating point literal is distinguished by its decimal point.
        private static string withDecimalPoint(string text)
        {
            return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
        }
    }

    private sealed class RawArg : AttributeArg
    {
        private readonly string _code;
        private readonly string _fsharpCode;

        public RawArg(string code, string? fsharpCode)
        {
            _code = code;
            _fsharpCode = fsharpCode ?? code;
        }

        public override string ToCSharp()
        {
            return _code;
        }

        public override string ToFSharp()
        {
            return _fsharpCode;
        }
    }
}

/// <summary>
///     A modeled attribute usage to be emitted on a generated type or generated method. Each
///     language writer renders the same declaration in its own syntax — <c>[Foo(...)]</c> in C#,
///     <c>[&lt;Foo(...)&gt;]</c> in F# — so an attribute added by an <see cref="ICodeFile" /> is
///     correct whichever language <c>codegen write</c> was asked for.
/// </summary>
/// <remarks>
///     This exists because the only previous way to put an attribute in front of a generated
///     declaration was to smuggle raw C# text through <see cref="GeneratedType.Header" /> /
///     <see cref="GeneratedMethod.Header" />, which silently corrupts F# output. <c>Header</c>
///     remains as an escape hatch. See jasperfx#743.
/// </remarks>
public class GeneratedAttribute
{
    public GeneratedAttribute(Type attributeType, params AttributeArg[] arguments)
    {
        if (attributeType == null)
        {
            throw new ArgumentNullException(nameof(attributeType));
        }

        if (!attributeType.CanBeCastTo<Attribute>())
        {
            throw new ArgumentOutOfRangeException(nameof(attributeType),
                $"{attributeType.FullNameInCode()} is not an Attribute type");
        }

        AttributeType = attributeType;
        Arguments = new List<AttributeArg>(arguments);
    }

    public Type AttributeType { get; }

    public IList<AttributeArg> Arguments { get; }

    /// <summary>
    ///     The full C# attribute usage, brackets included.
    /// </summary>
    public string ToCSharpDeclaration()
    {
        var name = AttributeArg.QualifyForCSharp(AttributeArg.TrimAttributeSuffix(AttributeType.FullNameInCode()));
        return Arguments.Count == 0
            ? $"[{name}]"
            : $"[{name}({Arguments.Select(x => x.ToCSharp()).Join(", ")})]";
    }

    /// <summary>
    ///     The full F# attribute usage, brackets included.
    /// </summary>
    public string ToFSharpDeclaration()
    {
        var name = AttributeArg.TrimAttributeSuffix(AttributeType.FSharpName());
        return Arguments.Count == 0
            ? $"[<{name}>]"
            : $"[<{name}({Arguments.Select(x => x.ToFSharp()).Join(", ")})>]";
    }

    /// <summary>
    ///     Write this attribute in the syntax of the supplied language.
    /// </summary>
    public void Write(ISourceWriter writer, CodegenLanguage language)
    {
        // WriteLine rather than Write: Write() substitutes '`' for '"', which would corrupt a
        // generic type name (List`1) appearing inside a raw or named-type argument.
        writer.WriteLine(language == CodegenLanguage.fsharp ? ToFSharpDeclaration() : ToCSharpDeclaration());
    }

    public IEnumerable<Assembly> AssemblyReferences()
    {
        yield return AttributeType.Assembly;

        foreach (var argument in Arguments)
        foreach (var assembly in argument.AssemblyReferences())
            yield return assembly;
    }

    public override string ToString()
    {
        return ToCSharpDeclaration();
    }
}
