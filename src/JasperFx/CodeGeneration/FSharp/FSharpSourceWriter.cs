using System.Buffers;
using System.Text;
using JasperFx.Core;

namespace JasperFx.CodeGeneration.FSharp;

/// <summary>
///     An <see cref="ISourceWriter" /> that lays out F# instead of C#. The only behavioral
///     difference from the C# <see cref="SourceWriter" /> is brace handling: a "block"
///     (<c>BLOCK:</c> marker or <see cref="FinishBlock" />) indents/dedents <b>without</b>
///     emitting any <c>{</c> / <c>}</c> characters, because F# uses significant whitespace for
///     <c>type</c> / <c>member</c> / <c>interface</c> scoping. Line writing, indentation, and the
///     backtick-to-double-quote substitution behave identically to <see cref="SourceWriter" />.
/// </summary>
/// <remarks>
///     Real F# braces (the <c>task { }</c> computation expression in particular) are genuine
///     syntax rather than scoping braces, so the emit code writes those as literal
///     <c>WriteLine("{")</c> / <c>WriteLine("}")</c> with manual <see cref="IndentionLevel" />
///     adjustment, bypassing the brace-free block protocol below.
/// </remarks>
public class FSharpSourceWriter : ISourceWriter, IDisposable
{
    private const int IndentSize = 4;
    private readonly StringBuilder _builder;

    public FSharpSourceWriter()
    {
        _builder = CodeGenerationObjectPool.StringBuilderPool.Get();
    }

    public void Dispose()
    {
        CodeGenerationObjectPool.StringBuilderPool.Return(_builder);
    }

    public int IndentionLevel { get; set; }

    public void BlankLine()
    {
        _builder.AppendLine();
    }

    public void Write(string? text = null)
    {
        if (text.IsEmpty())
        {
            BlankLine();
            return;
        }

        foreach (var (line, _) in text.SplitLines())
        {
            var buffer = ArrayPool<char>.Shared.Rent(line.Length);
            try
            {
                // constrain the span to the string length, this is important as the buffer returned might be larger than we need
                var bufferSpan = buffer.AsSpan(0, line.Length);
                line.Replace(bufferSpan, '`', '"');

                if (bufferSpan.IsEmpty)
                {
                    BlankLine();
                }
                else if (bufferSpan.StartsWith("BLOCK:"))
                {
                    // F#: emit the line and indent, but DO NOT open a '{'
                    WriteLine(bufferSpan.Slice(6));
                    IndentionLevel++;
                }
                else if (bufferSpan.StartsWith("END"))
                {
                    // F#: dedent only, no closing '}'
                    FinishBlock();
                }
                else
                {
                    WriteLine(bufferSpan);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
    }

    public void FinishBlock(ReadOnlySpan<char> extra = default)
    {
        if (IndentionLevel == 0)
        {
            throw new InvalidOperationException("Not currently in a code block");
        }

        // F# has no closing brace, so just dedent. The C#-only `extra` (e.g. "});")
        // is never passed by F# emit code and is intentionally ignored here.
        IndentionLevel--;
    }

    public void WriteLine(string text)
    {
        Indent();
        _builder.AppendLine(text);
    }

    public void WriteLine(ReadOnlySpan<char> value)
    {
        Indent();
        _builder.Append(value);
        _builder.AppendLine();
    }

    public void WriteLine(char value)
    {
        Indent();
        _builder.Append(value);
        _builder.AppendLine();
    }

    private void Indent()
    {
        _builder.Append(' ', IndentionLevel * IndentSize);
    }

    public string Code()
    {
        return _builder.ToString();
    }
}
