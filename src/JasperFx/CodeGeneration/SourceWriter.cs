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

        text.ReadLines(line =>
        {
            line = line.Replace('`', '"');

            if (line.IsEmpty())
            {
                BlankLine();
            }
            else if (line.StartsWith("BLOCK:"))
            {
                WriteLine(line.AsSpan(6));
                StartBlock();
            }
            else if (line.StartsWith("END"))
            {
                FinishBlock(line.AsSpan(3));
            }
            else
            {
                WriteLine(line);
            }
        });
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