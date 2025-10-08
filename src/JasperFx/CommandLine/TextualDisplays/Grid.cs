namespace JasperFx.CommandLine.TextualDisplays;

public class Grid<T>
{
    private readonly List<Column<T>> _columns = new();

    public void AddColumn(string header, Func<T, string> source, bool rightJustified = false)
    {
        var column = new Column<T>(header, source)
        {
            RightJustified = rightJustified
        };

        _columns.Add(column);
    }

    public string Write(IReadOnlyList<T> items)
    {
        var writer = new StringWriter();

        var totalWidth = determineWidths(items);
        writer.WriteLine();
        writeSolidLine(writer, totalWidth);

        writeHeaderRow(writer);

        writeSolidLine(writer, totalWidth);

        foreach (var item in items)
        {
            writeBodyRow(writer, item);
        }

        writeSolidLine(writer, totalWidth);

        return writer.ToString();
    }

    private int determineWidths(IReadOnlyList<T> items)
    {
        foreach (var column in _columns)
        {
            column.DetermineWidth(items);
        }

        var totalWidth = _columns.Sum(x => x.Width) + (_columns.Count) + 1;
        return totalWidth;
    }

    private void writeBodyRow(StringWriter writer, T item)
    {
        foreach (var column in _columns)
        {
            column.WriteLine(writer, item);
        }

        writer.WriteLine('|');
    }

    private void writeHeaderRow(StringWriter writer)
    {
        foreach (var column in _columns)
        {
            column.WriteHeader(writer);
        }

        writer.WriteLine('|');
    }

    private static void writeSolidLine(StringWriter writer, int totalWidth)
    {
        writer.WriteLine("".PadRight(totalWidth, '-'));
    }
}