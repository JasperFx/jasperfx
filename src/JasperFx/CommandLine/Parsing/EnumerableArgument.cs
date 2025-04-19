using System.Collections;
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