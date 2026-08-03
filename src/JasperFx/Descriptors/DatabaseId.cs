using System.Text;

namespace JasperFx.Descriptors;

public record DatabaseId(string Server, string Name)
{
    // GH-599: one identity, one spelling. This used to be the unescaped "{Server}.{Name}" while ToString()
    // produced an escaped form, so a single database reached a client spelled two ways depending on which
    // one it travelled as, and joins between the two silently missed (see CritterWatch#878).
    public string Identity => ToString();

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

    // GH-599: the escaped form has to survive a System.Uri round trip, because the only consumer of it
    // interpolates it into an agent URI (Wolverine's EventSubscriptionAgentFamily.UriFor) and parses it
    // back out of uri.Segments. '.' is *unreserved* in RFC 3986, so Uri canonicalisation decodes "%2E"
    // straight back to '.' -- the escaping this used to do delivered none of the disambiguation it was
    // written for. '!' is a sub-delimiter, which Uri preserves verbatim, so it does.
    //
    // Order matters. '!' and '~' are the two escape characters, so a literal one has to be escaped before
    // anything else is allowed to produce one:
    //
    //   %  -> %25    ("%25" survives Uri canonicalisation; only percent-encoded *unreserved* chars don't)
    //   !  -> !!
    //   ~  -> !~
    //   /  -> ~      (unchanged; already survived, and existing persisted URIs use it)
    //   .  -> !
    private static string EscapeSegment(string value)
    {
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("!", "!!", StringComparison.Ordinal)
            .Replace("~", "!~", StringComparison.Ordinal)
            .Replace("/", "~", StringComparison.Ordinal)
            .Replace(".", "!", StringComparison.Ordinal);
    }

    private static string UnescapeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c == '!' && i + 1 < value.Length && (value[i + 1] == '!' || value[i + 1] == '~'))
            {
                builder.Append(value[i + 1]);
                i++;
            }
            else if (c == '!')
            {
                builder.Append('.');
            }
            else if (c == '~')
            {
                // A bare '~' is '/' -- both in the current grammar and in every URI persisted before it.
                builder.Append('/');
            }
            else
            {
                builder.Append(c);
            }
        }

        // "%2E" is only ever produced by a version of this type that predates GH-599, but those spellings
        // are persisted in agent URIs, so keep decoding them. It has to run before the "%25" pass: a value
        // holding the literal text "%2E" is written as "%252E", which contains no "%2E" of its own and so
        // survives this pass untouched, then decodes correctly in the next one.
        return builder.ToString()
            .Replace("%2E", ".", StringComparison.OrdinalIgnoreCase)
            .Replace("%25", "%", StringComparison.OrdinalIgnoreCase);
    }
}
