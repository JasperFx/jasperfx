namespace JasperFx.CommandLine.TextualDisplays;

internal class Column<T>
{
    private readonly Func<T, string> _source;
    private int _width;
    public string Header { get; }

    public Column(string header, Func<T, string> source)
    {
        _source = source;
        Header = header;
    }

    public bool RightJustified { get; set; }

    public void DetermineWidth(IEnumerable<T> items)
    {
        _width = items.Select(x => _source(x)).Where(x => x != null).Max(x => x.Length);
        if (Header.Length > _width) _width = Header.Length;
        _width += 4;
    }

    public int Width => _width;

    public void WriteHeader(TextWriter writer)
    {
        writer.Write("| ");
        writer.Write(Header);
        writer.Write("".PadRight(_width - Header.Length - 1));
    }

    public void WriteLine(TextWriter writer, T item)
    {
        var value = _source(item) ?? string.Empty;
        writer.Write("| ");
        if (RightJustified)
        {
            writer.Write("".PadRight(_width - value.Length - 1));
            writer.Write(value);
        }
        else
        {
            writer.Write(value);
            writer.Write("".PadRight(_width - value.Length - 1));
        }
    }
}