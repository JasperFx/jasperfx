using System.Buffers;
using System.Text;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

public class SourceWriter : ISourceWriter, IDisposable
{
    private readonly StringBuilder _builder;

    // GH-956: a blank line is held back until the next line is known, so it can be dropped where a person
    // would never put one — before a closing brace, else / catch / finally, right after an opening brace,
    // at the top of the output, at the end of it, or next to another blank line.
    private bool _pendingBlankLine;
    private bool _atStartOfBlock = true;

    public SourceWriter()
    {
        _builder = CodeGenerationObjectPool.StringBuilderPool.Get();
    }

    private const int IndentSize = 4;

    public void Dispose()
    {
        CodeGenerationObjectPool.StringBuilderPool.Return(_builder);
    }

    public int IndentionLevel { get; set; }

    public void BlankLine()
    {
        _pendingBlankLine = true;
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
                    continue;
                }

                switch (SourceWriterDirective.Parse(bufferSpan, out var content))
                {
                    case SourceWriterDirectiveKind.Block:
                        WriteLine(content);
                        StartBlock();
                        break;

                    case SourceWriterDirectiveKind.End:
                        FinishBlock(content);
                        break;

                    default:
                        WriteLine(content);
                        break;
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
            
        }
    }

    public void WriteLine(string text)
    {
        startLine(text);
        _builder.AppendLine(text);
    }

    public void WriteLine(ReadOnlySpan<char> value)
    {
        startLine(value);
        _builder.Append(value);
        _builder.AppendLine();
    }

    public void WriteLine(char value)
    {
        startLine([value]);
        _builder.Append(value);
        _builder.AppendLine();
    }

    private void startLine(ReadOnlySpan<char> line)
    {
        if (_pendingBlankLine && !_atStartOfBlock && !continuesThePreviousBlock(line.TrimStart()))
        {
            _builder.AppendLine();
        }

        _pendingBlankLine = false;
        _atStartOfBlock = false;
        _builder.Append(' ', IndentionLevel * IndentSize);
    }

    private static bool continuesThePreviousBlock(ReadOnlySpan<char> line)
        => line.StartsWith("}") || startsWithKeyword(line, "else") || startsWithKeyword(line, "catch")
           || startsWithKeyword(line, "finally");

    private static bool startsWithKeyword(ReadOnlySpan<char> line, string keyword)
        => line.StartsWith(keyword)
           && (line.Length == keyword.Length || !(char.IsLetterOrDigit(line[keyword.Length]) || line[keyword.Length] == '_'));

    public void FinishBlock(ReadOnlySpan<char> extra = default)
    {
        if (IndentionLevel == 0)
        {
            throw new InvalidOperationException("Not currently in a code block");
        }

        IndentionLevel--;

        if (extra.IsEmpty)
        {
            WriteLine('}');
        }
        else
        {
            WriteLine($"}}{extra}");
        }

        BlankLine();
    }

    private void StartBlock()
    {
        WriteLine('{');
        IndentionLevel++;
        _atStartOfBlock = true;
    }

    public string Code()
    {
        return _builder.ToString();
    }

    internal class BlockMarker : IDisposable
    {
        private readonly SourceWriter _parent;

        public BlockMarker(SourceWriter parent)
        {
            _parent = parent;
        }

        public void Dispose()
        {
            _parent.FinishBlock();
        }
    }
}