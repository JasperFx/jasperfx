using JasperFx.Core;

namespace JasperFx.CommandLine.Parsing.Generated;

/// <summary>
/// Source-generated enumerable flag handler. Replaces EnumerableFlag
/// with pre-computed aliases, direct setter, and inlined converter.
/// </summary>
public sealed class GeneratedEnumerableFlag<T> : ITokenHandler
{
    private readonly FlagAliases _aliases;
    private readonly Action<object, List<T>> _setter;
    private readonly Func<string, T> _converter;

    public GeneratedEnumerableFlag(string memberName, string description, FlagAliases aliases,
        Action<object, List<T>> setter, Func<string, T> converter)
    {
        MemberName = memberName;
        Description = description;
        _aliases = aliases;
        _setter = setter;
        _converter = converter;
    }

    public string Description { get; }
    public string MemberName { get; }

    public bool Handle(object input, Queue<string> tokens)
    {
        if (tokens.Count == 0 || !_aliases.Matches(tokens.Peek()))
            return false;

        var flag = tokens.Dequeue();
        var list = new List<T>();
        var wasHandled = false;

        while (tokens.Count > 0 && !tokens.NextIsFlag())
        {
            var value = _converter(tokens.Dequeue());
            list.Add(value);
            wasHandled = true;
        }

        if (!wasHandled)
            throw new InvalidUsageException($"No values specified for flag {flag}.");

        _setter(input, list);
        return true;
    }

    public string ToUsageDescription()
    {
        var name = InputParser.RemoveFlagSuffix(MemberName).ToLower();
        return $"[{_aliases} <{name}1 {name}2 {name}3 ...>]";
    }
}
