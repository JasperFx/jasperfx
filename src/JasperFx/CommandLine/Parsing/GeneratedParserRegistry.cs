namespace JasperFx.CommandLine.Parsing;

/// <summary>
/// Registry for source-generated input parsers. The source generator emits
/// registration calls that populate this registry at startup. UsageGraph
/// checks here before falling back to reflection-based InputParser.
/// </summary>
public static class GeneratedParserRegistry
{
    private static readonly Dictionary<Type, IGeneratedInputParser> _parsers = new();

    public static void Register(IGeneratedInputParser parser)
    {
        _parsers[parser.InputType] = parser;
    }

    public static List<ITokenHandler>? TryGetHandlers(Type inputType)
    {
        return _parsers.TryGetValue(inputType, out var parser) ? parser.BuildHandlers() : null;
    }

    /// <summary>
    /// Returns true if a generated parser exists for the given input type.
    /// </summary>
    public static bool HasParser(Type inputType)
    {
        return _parsers.ContainsKey(inputType);
    }

    /// <summary>
    /// Clears all registered parsers. Used for testing.
    /// </summary>
    public static void Clear()
    {
        _parsers.Clear();
    }
}
