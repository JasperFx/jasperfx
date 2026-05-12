using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using JasperFx.Core.Reflection;

namespace JasperFx.CommandLine.Internal.Conversion;

public class StringConverterProvider : IConversionProvider
{
    [UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
        Justification = "Looks up a public constructor that takes a single string. Only invoked through Conversions, reached from annotated CommandLine entry points (CommandFactory.BuildRun, InputParser.GetHandlers / BuildHandler). AOT consumers see the annotation at those entry points; types reached through input-model property scanning are preserved via the input type itself.")]
    public Func<string, object>? ConverterFor(Type type)
    {
        if (!type.IsConcrete())
        {
            return null;
        }

        var constructor = type.GetConstructor(new[] { typeof(string) });
        if (constructor == null)
        {
            return null;
        }

        var param = Expression.Parameter(typeof(string), "arg");
        var body = Expression.New(constructor, param);

        return Expression.Lambda<Func<string, object>>(body, param).Compile();
    }
}