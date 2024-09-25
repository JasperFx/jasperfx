using JasperFx.CodeGeneration.Model;

namespace JasperFx.CodeGeneration;

/// <summary>
///     Literal representation of a primitive value in code
/// </summary>
public class Value : Variable
{
    public Value(object value) : base(value.GetType(), RepresentationInCode(value))
    {
    }

    public static string RepresentationInCode(object value)
    {
        if (value is string s)
        {
            return $"\"{value}\"";
        }

        if (value is int i)
        {
            return value.ToString()!;
        }

        if (value is double d)
        {
            return value.ToString()!;
        }

        throw new NotSupportedException(
            $"The Value placeholder does not (yet) support values of type {value.GetType()}");
    }
}