namespace JasperFx.CommandLine.Parsing.Generated;

/// <summary>
/// Source-generated boolean flag handler. Replaces BooleanFlag by using a
/// pre-computed FlagAliases and a direct setter delegate instead of reflection.
/// </summary>
public sealed class GeneratedBooleanFlag : ITokenHandler
{
    private readonly FlagAliases _aliases;
    private readonly Action<object> _setter;

    public GeneratedBooleanFlag(string memberName, string description, FlagAliases aliases, Action<object> setter)
    {
        MemberName = memberName;
        Description = description;
        _aliases = aliases;
        _setter = setter;
    }

    public string Description { get; }
    public string MemberName { get; }

    public bool Handle(object input, Queue<string> tokens)
    {
        if (tokens.Count == 0 || !_aliases.Matches(tokens.Peek()))
            return false;

        tokens.Dequeue();
        _setter(input);
        return true;
    }

    public string ToUsageDescription()
    {
        return $"[{_aliases}]";
    }
}
