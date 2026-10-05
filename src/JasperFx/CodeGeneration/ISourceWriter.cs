namespace JasperFx.CodeGeneration;

public interface ISourceWriter
{
    /// <summary>
    ///     Set or read the current indention level for the code
    ///     being generated
    /// </summary>
    int IndentionLevel { get; set; }

    /// <summary>
    ///     Writes a blank line into the code being generated. <see cref="SourceWriter" /> holds it back until
    ///     the next line is known and drops it where a person would not put one: before a closing brace or
    ///     <c>else</c> / <c>catch</c> / <c>finally</c>, right after an opening brace, at the start or end of
    ///     the output, or next to another blank line.
    /// </summary>
    void BlankLine();

    /// <summary>
    ///     Writes one or more lines into the code that respects the current block depth
    ///     and handles text alignment for you. A line of <c>BLOCK:text</c> writes <c>text</c> and opens a
    ///     block; a line of <c>END</c> — alone, or followed by text that cannot continue an identifier, such
    ///     as <c>END;</c> or <c>END while (x);</c> — closes one. Leading whitespace before either directive is
    ///     ignored. Every backtick is written as a double quote; use <see cref="WriteLine(string)" /> to write
    ///     a line verbatim.
    /// </summary>
    /// <param name="text"></param>
    void Write(string? text = null);

    /// <summary>
    ///     Writes a line with a closing '}' character at the current block level
    ///     and decrements the current block level
    /// </summary>
    /// <param name="extra"></param>
    void FinishBlock(ReadOnlySpan<char> extra = default);


    /// <summary>
    ///     Writes a single line with this content to the code
    ///     at the current block level, verbatim: no directives, and backticks are kept
    /// </summary>
    /// <param name="text"></param>
    void WriteLine(string text);

    /// <summary>
    ///     Writes a single line with this content to the code
    ///     at the current block level
    /// </summary>
    /// <param name="text"></param>
    void WriteLine(ReadOnlySpan<char> value);

    /// <summary>
    ///     Writes a single line with this content to the code
    ///     at the current block level
    /// </summary>
    /// <param name="text"></param>
    void WriteLine(char value);
}