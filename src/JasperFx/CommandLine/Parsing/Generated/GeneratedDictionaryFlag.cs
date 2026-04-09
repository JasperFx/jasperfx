namespace JasperFx.CommandLine.Parsing.Generated;

/// <summary>
/// Source-generated dictionary flag handler. Replaces DictionaryFlag
/// with pre-computed prefix and direct getter/setter delegates.
/// </summary>
public sealed class GeneratedDictionaryFlag : ITokenHandler
{
    private readonly string _prefix;
    private readonly Func<object, IDictionary<string, string>?> _getter;
    private readonly Action<object, IDictionary<string, string>> _setter;

    public GeneratedDictionaryFlag(string memberName, string description, FlagAliases aliases,
        Func<object, IDictionary<string, string>?> getter,
        Action<object, IDictionary<string, string>> setter)
    {
        MemberName = memberName;
        Description = description;
        _prefix = aliases.LongForm + ":";
        _getter = getter;
        _setter = setter;
    }

    public string Description { get; }
    public string MemberName { get; }

    public bool Handle(object input, Queue<string> tokens)
    {
        if (tokens.Count == 0 || !tokens.Peek().StartsWith(_prefix))
            return false;

        var flag = tokens.Dequeue();

        if (tokens.Count == 0)
            throw new InvalidUsageException($"No value specified for flag {flag}.");

        var key = flag.Split(':').Last().Trim();
        var rawValue = tokens.Dequeue();

        var dict = _getter(input);
        if (dict == null)
        {
            dict = new Dictionary<string, string>();
            _setter(input, dict);
        }

        dict[key] = rawValue;
        return true;
    }

    public string ToUsageDescription()
    {
        return $"[{_prefix}<prop> <value>]";
    }
}
