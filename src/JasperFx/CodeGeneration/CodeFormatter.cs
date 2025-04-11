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
            return value.GetType().FullNameInCode() + "." + value;
        }

        if (value.GetType() == typeof(string[]))
        {
            var array = (string[])value;
            return $"new string[]{{{array.Select(Write).Join(", ")}}}";
        }

        if (value.GetType().IsArray)
        {
            var code = $"new {value.GetType().GetElementType().FullNameInCode()}[]{{";

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
}