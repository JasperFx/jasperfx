using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;

namespace JasperFx.CommandLine.Internal.Conversion;

public class ArrayConversion : IConversionProvider
{
    private readonly Conversions _conversions;

    public ArrayConversion(Conversions conversions)
    {
        _conversions = conversions;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Array.CreateInstance is only invoked through Conversions, which is itself reached only from annotated CommandLine entry points (CommandFactory.BuildRun, InputParser.GetHandlers / BuildHandler). AOT consumers see the annotation at those entry points.")]
    public Func<string, object>? ConverterFor(Type type)
    {
        if (!type.IsArray)
        {
            return null;
        }

        var innerType = type.GetElementType()!;
        var inner = _conversions.FindConverter(innerType);

        return stringValue =>
        {
            if (stringValue.ToUpper() == "EMPTY" || stringValue.Trim().IsEmpty())
            {
                return Array.CreateInstance(innerType, 0);
            }

            var strings = stringValue.ToDelimitedArray();
            var array = Array.CreateInstance(innerType, strings.Length);

            for (var i = 0; i < strings.Length; i++)
            {
                var value = inner?.Invoke(strings[i]);
                array.SetValue(value, i);
            }

            return array;
        };
    }
}