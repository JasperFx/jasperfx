namespace JasperFx.CodeGeneration;

internal enum SourceWriterDirectiveKind
{
    Line,
    Block,
    End
}

/// <summary>
///     Parses the <c>BLOCK:</c> and <c>END</c> directives that <see cref="ISourceWriter.Write" /> recognises,
///     shared by the C# and F# writers so the two can never disagree about what a directive is (GH-956).
/// </summary>
internal static class SourceWriterDirective
{
    /// <summary>
    ///     Classify one line of <see cref="ISourceWriter.Write" /> input. <paramref name="content" /> is the
    ///     block's opening line for <see cref="SourceWriterDirectiveKind.Block" />, the text after the closing
    ///     brace for <see cref="SourceWriterDirectiveKind.End" /> (<c>END;</c> → <c>;</c>), and the line itself,
    ///     untrimmed, for an ordinary line.
    /// </summary>
    public static SourceWriterDirectiveKind Parse(ReadOnlySpan<char> line, out ReadOnlySpan<char> content)
    {
        // Leading whitespace is common in multi-line Write() input; it never made a directive literal text.
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("BLOCK:"))
        {
            content = trimmed.Slice(6);
            return SourceWriterDirectiveKind.Block;
        }

        // END is a directive only on its own or followed by something that cannot continue an identifier,
        // so `END;`, `END)` and `END while (x);` close a block while `ENDPOINT_COUNT = 3;` is just code.
        if (trimmed.StartsWith("END") && (trimmed.Length == 3 || !isIdentifierCharacter(trimmed[3])))
        {
            content = trimmed.Slice(3).TrimEnd();
            return SourceWriterDirectiveKind.End;
        }

        content = line;
        return SourceWriterDirectiveKind.Line;
    }

    private static bool isIdentifierCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
}
