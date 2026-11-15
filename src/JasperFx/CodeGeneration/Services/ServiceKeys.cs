using System.Globalization;
using System.Text;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

/// <summary>
/// How code generation matches a <see cref="FromKeyedServicesAttribute" /> key to a keyed registration,
/// and how it writes that key back into generated code. Kept in one place because the codegen paths
/// used to disagree: one compared keys with <see cref="object.Equals(object, object)" /> after turning the
/// attribute's key into a string, so an enum key never matched, and another compared both sides'
/// <c>ToString()</c> (jasperfx#1189).
/// </summary>
internal static class ServiceKeys
{
    /// <summary>
    /// The keyed registration of <paramref name="registrations" /> that <paramref name="key" /> selects.
    /// An exact match by <see cref="object.Equals(object, object)" /> wins, as it does in
    /// Microsoft.Extensions.DependencyInjection. Failing that, a registration whose key has the same
    /// <c>ToString()</c> is still accepted, which is the only comparison constructor plans used to make:
    /// generated code names the registration's own key (see <see cref="WriteAttributeArgument" />), so
    /// the instance resolved at runtime is the one codegen planned for.
    /// </summary>
    public static ServiceDescriptor? FindKeyed(IEnumerable<ServiceDescriptor> registrations, object key)
    {
        var keyed = registrations.Where(x => x.IsKeyedService).ToArray();

        return keyed.FirstOrDefault(x => Equals(x.ServiceKey, key))
               ?? keyed.FirstOrDefault(x => x.ServiceKey?.ToString() == key.ToString());
    }

    /// <summary>
    /// <paramref name="key" /> as a C# attribute argument with its own type: an enum key is written as the
    /// enum member, not as its name in quotes, because the container matches keys by type as well as value.
    /// A key that cannot be an attribute argument is written as its <c>ToString()</c> in quotes, as every
    /// key was before jasperfx#1189.
    /// </summary>
    public static string WriteAttributeArgument(object? key)
    {
        return key switch
        {
            null => "null",
            string s => stringLiteral(s),
            bool b => b ? "true" : "false",
            char c => charLiteral(c),
            Enum e => CodeFormatter.Write(e),
            Type t => $"typeof({t.FullNameInCode()})",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            uint u => u.ToString(CultureInfo.InvariantCulture) + "U",
            ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
            short sh => $"(short){sh.ToString(CultureInfo.InvariantCulture)}",
            ushort us => $"(ushort){us.ToString(CultureInfo.InvariantCulture)}",
            byte by => $"(byte){by.ToString(CultureInfo.InvariantCulture)}",
            sbyte sb => $"(sbyte){sb.ToString(CultureInfo.InvariantCulture)}",
            double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture) + "D",
            float f when float.IsFinite(f) => f.ToString("R", CultureInfo.InvariantCulture) + "F",
            _ => stringLiteral(key.ToString() ?? string.Empty)
        };
    }

    private static string stringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            builder.Append(c == '\'' ? "'" : escape(c));
        }

        return builder.Append('"').ToString();
    }

    private static string charLiteral(char value) => "'" + (value == '"' ? "\"" : escape(value)) + "'";

    private static string escape(char c)
    {
        return c switch
        {
            '\\' => @"\\",
            '"' => "\\\"",
            '\'' => @"\'",
            '\0' => @"\0",
            '\n' => @"\n",
            '\r' => @"\r",
            '\t' => @"\t",
            _ when char.IsControl(c) || char.IsSurrogate(c) => $"\\u{(int)c:X4}",
            _ => c.ToString()
        };
    }
}
