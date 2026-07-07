namespace JasperFx.Descriptors;

public record DatabaseId(string Server, string Name)
{
    public string Identity => $"{Server}.{Name}";

    public static DatabaseId Parse(string text)
    {
        if (TryParse(text, out var id))
        {
            return id;
        }

        throw new FormatException($"Invalid database id '{text}'");
    }

    public static bool TryParse(string text, out DatabaseId id)
    {
        var separator = text.LastIndexOf('.');

        // A leading separator (empty server) or no separator at all is malformed, but a trailing
        // separator is a legitimate empty database name. The ctor accepts an empty Name (e.g. a
        // Postgres connection string with no Database= yields one), so Parse must round-trip it
        // rather than throwing when the agent URI is parsed back. See wolverine#3170.
        if (separator <= 0)
        {
            id = default!;
            return false;
        }

        var server = text[..separator];
        var name = text[(separator + 1)..];

        id = new DatabaseId(UnescapeSegment(server), UnescapeSegment(name));
        return true;
    }

    public override string ToString()
    {
        return $"{EscapeSegment(Server)}.{EscapeSegment(Name)}";
    }

    private static string EscapeSegment(string value)
    {
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("/", "~", StringComparison.Ordinal)
            .Replace(".", "%2E", StringComparison.Ordinal);
    }

    private static string UnescapeSegment(string value)
    {
        return value
            .Replace("%2E", ".", StringComparison.OrdinalIgnoreCase)
            .Replace("~", "/", StringComparison.Ordinal)
            .Replace("%25", "%", StringComparison.OrdinalIgnoreCase);
    }
}
