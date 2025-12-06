using System.Buffers;
using System.Text;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

public class SourceWriter : ISourceWriter, IDisposable
{
    private readonly StringBuilder _builder;

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
                    WriteLine(bufferSpan.Slice(6));
                    StartBlock();
                }
                else if (bufferSpan.StartsWith("END"))
                {
                    FinishBlock(bufferSpan.Slice(3));
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