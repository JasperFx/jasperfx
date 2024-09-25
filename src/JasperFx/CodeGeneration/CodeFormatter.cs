using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration;

public static class CodeFormatter
{
    public static string Write(object? value)
    {
        // TODO -- add Guid, int, double, long, bool

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
            return value.GetType().FullNameInCode() + "." + value;
        }

        if (value is Type t)
        {
            return $"typeof({t.FullNameInCode()})";
        }

        return value.ToString()!;
    }
}