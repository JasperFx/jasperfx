using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CommandLine.Internal.Conversion;
using JasperFx.Core.Reflection;

namespace JasperFx.CommandLine.Parsing;

public class EnumerableArgument : Argument
{
    private readonly MemberInfo _member;

    public EnumerableArgument(MemberInfo member, Conversions conversions) : base(member, conversions)
    {
        _member = member;

        _converter = conversions.FindConverter(member.GetMemberType()!.DetermineElementType()!)!;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Reachable only through InputParser.BuildHandler, which carries RequiresUnreferencedCode + RequiresDynamicCode and is itself reached from the annotated CommandFactory.BuildRun / RegisterCommand entry points. The List<elementType> closure is intrinsic and the trimmer keeps the closed generic when List<T> is preserved.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Same as IL2026 — the CloseAndBuildAs call closes List<elementType> via MakeGenericType, which AOT consumers see through the annotated entry points.")]
    public override bool Handle(object input, Queue<string> tokens)
    {
        var elementType = _member.GetMemberType()!.GetGenericArguments().First();
        var list = typeof(List<>).CloseAndBuildAs<IList>(elementType);

        var wasHandled = false;
        while (tokens.Count > 0 && !tokens.NextIsFlag())
        {
            var value = _converter(tokens.Dequeue());
            list.Add(value);

            wasHandled = true;
        }

        if (wasHandled)
        {
            setValue(input, list);
        }

        return wasHandled;
    }

    public override string ToUsageDescription()
    {
        var name = _member.Name.ToLower();
        return $"<{name}1 {name}2 {name}3 ...>";
    }
}