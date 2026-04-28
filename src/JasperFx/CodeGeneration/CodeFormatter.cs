using System.Globalization;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration;

public static class CodeFormatter
{
    public static string Write(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is Variable v)
        {
            return v.Usage;
        }

        if (value is string)
        {
            return "\"" + value + "\"";
        }

        if (value.GetType().IsEnum)
        {
            return WriteEnum((Enum)value);
        }

        if (value.GetType() == typeof(string[]))
        {
            var array = (string[])value;
            return $"new string[]{{{array.Select(Write).Join(", ")}}}";
        }

        if (value.GetType().IsArray)
        {
            var code = $"new {value.GetType().GetElementType()!.FullNameInCode()}[]{{";

            var enumerable = (Array)value;
            switch (enumerable.Length)
            {
                case 0:
                    
                    break;
                case 1:
                    code += Write(enumerable.GetValue(0));
                    break;

                default:
                    for (int i = 0; i < enumerable.Length - 1; i++)
                    {
                        code += Write(enumerable.GetValue(i));
                        code += ", ";
                    }

                    code += Write(enumerable.GetValue(enumerable.Length - 1));
                    break;
            }
            
            code += "}";

            return code;
        }

        if (value is Type t)
        {
            return $"typeof({t.FullNameInCode()})";
        }

        return value.ToString()!;
    }

    /// <summary>
    /// Render an enum value as valid C# source. Handles three cases safely:
    /// (1) a single defined named member ⇒ <c>Namespace.Type.Name</c>;
    /// (2) a <see cref="FlagsAttribute"/> combination whose bits all map to defined members ⇒ <c>Type.A | Type.B</c>;
    /// (3) any other value (including bit-or'd values on non-Flags enums like <c>NpgsqlDbType</c>) ⇒ a checked cast
    /// of the underlying integer literal: <c>((Type)(rawValue))</c>. The cast form is necessary because
    /// <see cref="Enum.ToString()"/> returns the integer literal as a string for undefined values, which is not a
    /// valid C# identifier and used to produce uncompilable code such as <c>NpgsqlDbType.-2147483629</c>.
    /// </summary>
    private static string WriteEnum(Enum value)
    {
        var enumType = value.GetType();
        var typeName = enumType.FullNameInCode();

        // Case 1: defined single member.
        if (Enum.IsDefined(enumType, value))
        {
            return typeName + "." + value;
        }

        // Case 2: [Flags] enum where ToString() yields comma-separated names.
        if (enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var asString = value.ToString();
            if (asString.Length > 0 && !IsNumericLiteral(asString))
            {
                var parts = asString.Split(", ");
                return string.Join(" | ", parts.Select(p => typeName + "." + p));
            }
        }

        // Case 3: undefined value — emit a cast of the underlying integer literal.
        var underlying = Enum.GetUnderlyingType(enumType);
        var raw = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        return $"(({typeName})({raw}))";
    }

    private static bool IsNumericLiteral(string text)
    {
        var i = 0;
        if (text[0] == '-' || text[0] == '+')
        {
            if (text.Length == 1) return false;
            i = 1;
        }
        for (; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) return false;
        }
        return true;
    }
}