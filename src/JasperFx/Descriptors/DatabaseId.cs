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
        if (separator <= 0 || separator == text.Length - 1)
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
