using JasperFx.Core;

namespace JasperFx.CommandLine.Parsing.Generated;

/// <summary>
/// Source-generated flag handler. Replaces Flag by using a pre-computed FlagAliases,
/// a direct setter delegate, and an inlined converter instead of reflection.
/// </summary>
public sealed class GeneratedFlag<T> : ITokenHandler
{
    private readonly FlagAliases _aliases;
    private readonly Action<object, T> _setter;
    private readonly Func<string, T> _converter;
    private readonly bool _isEnum;
    private readonly string _flagNameForUsage;

    public GeneratedFlag(string memberName, string description, FlagAliases aliases,
        Action<object, T> setter, Func<string, T> converter, bool isEnum = false)
    {
        MemberName = memberName;
        Description = description;
        _aliases = aliases;
        _setter = setter;
        _converter = converter;
        _isEnum = isEnum;
        _flagNameForUsage = InputParser.RemoveFlagSuffix(memberName).ToLower();
    }

    public string Description { get; }
    public string MemberName { get; }

    public bool Handle(object input, Queue<string> tokens)
    {
        if (tokens.Count == 0 || !_aliases.Matches(tokens.Peek()))
            return false;

        var flag = tokens.Dequeue();

        if (tokens.Count == 0)
            throw new InvalidUsageException($"No value specified for flag {flag}.");

        var rawValue = tokens.Dequeue();
        var value = _converter(rawValue);
        _setter(input, value);
        return true;
    }

    public string ToUsageDescription()
    {
        if (_isEnum)
        {
            // _isEnum is computed from the underlying type, so a nullable-enum flag (T = SomeEnum?)
            // lands here with typeof(T) == Nullable<SomeEnum>, which is not itself an enum.
            // Enum.GetNames would throw "Type provided must be an Enum". Resolve the underlying
            // enum type first. See jasperfx#441 (NetCoreInput.LogLevelFlag is LogLevel?).
            var enumType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            var enumValues = Enum.GetNames(enumType).Join("|");
            return $"[{_aliases} {enumValues}]";
        }

        return $"[{_aliases} <{_flagNameForUsage}>]";
    }
}
