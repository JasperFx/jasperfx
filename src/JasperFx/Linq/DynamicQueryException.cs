namespace JasperFx.Linq;

/// <summary>
/// A <see cref="DynamicQueryText" /> that could not be turned into a query: it failed to parse, it named a
/// member the element type does not have, or it was refused by the <see cref="DynamicQueryPolicy" />.
/// </summary>
/// <remarks>
/// <see cref="Reason" /> is written for the person who typed the text — show it next to the input, at
/// <see cref="Position" /> when there is one, rather than as a generic failure.
/// </remarks>
public class DynamicQueryException : Exception
{
    public DynamicQueryException(DynamicQueryClause clause, string text, string reason, int? position = null,
        Exception? inner = null)
        : base(BuildMessage(clause, text, reason, position), inner)
    {
        Clause = clause;
        Text = text;
        Reason = reason;
        Position = position;
    }

    /// <summary>Which half of the query the problem is in.</summary>
    public DynamicQueryClause Clause { get; }

    /// <summary>The text of that half, exactly as it was given.</summary>
    public string Text { get; }

    /// <summary>What is wrong, without the surrounding context <see cref="Exception.Message" /> adds.</summary>
    public string Reason { get; }

    /// <summary>
    /// The zero-based character offset into <see cref="Text" /> the parser stopped at, when the problem is a
    /// parse failure. Null for a refusal made after parsing, which is about a shape rather than a place.
    /// </summary>
    public int? Position { get; }

    private static string BuildMessage(DynamicQueryClause clause, string text, string reason, int? position)
        => position is { } at
            ? $"{clause} '{text}' at position {at}: {reason}"
            : $"{clause} '{text}': {reason}";
}
