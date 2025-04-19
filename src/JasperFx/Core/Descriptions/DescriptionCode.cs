using JasperFx.CommandLine.Descriptions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace JasperFx.Core.Descriptions;

/*
 * TODOs
 * Markdown writer
 * HtmlTag equivalent???
 * Write MarkUp for spectre
 *
 *
 *
 * Link
 * Bullet lists
 */

public class Description
{
    public string Title { get; }

    public Description(string title)
    {
        Title = title;
    }

    public List<IRenderable> Items = new();

    public void WriteToConsole()
    {
        // TODO -- write the title too
        foreach (var item in Items)
        {
            var renderable = item.BuildConsoleDisplay();
            AnsiConsole.Write(renderable);
        }
    }

    public static void WriteManyToConsole(IEnumerable<Description> descriptions)
    {
        
    }

    public Table AddTable(string? title = null)
    {
        var table = new Table{Title = title};
        Items.Add(table);
        return table;
    }
}

/// <summary>
/// Marker interface 
/// </summary>
public interface IRenderable
{
    Renderable BuildConsoleDisplay();
}

public enum HighlightMode
{
    Success,
    Fail,
    Warning,
    None
}

public class Tree : Fragment
{
    public string Text { get; }

    public Tree(string text)
    {
        Text = text;
    }

    public List<IRenderable> Items { get; } = new();

    public Tree AddNode(string text)
    {
        var span = new Span(text);
        Items.Add(span);
        return this;
    }

    public Tree AddNode(IRenderable renderable)
    {
        Items.Add(renderable);
        return this;
    }
}

public class Span : Fragment, IRenderable
{
    public Span(string text)
    {
        Text = text;
    }

    public string Text { get; set; }
    
    // TODO -- do something with this later
    public string? LinkUrl { get; set; }
    public Renderable BuildConsoleDisplay()
    {
        var markup = WrapText(Text);
        return markup;
    }
}

public class Line : Fragment
{
    public Line(string text)
    {
        Text = text;
    }

    public string Text { get; set; }
}

public class Sentence : Fragment
{
    public List<Span> Spans { get; } = new();
}

public class Table : IRenderable
{
    public string? Title { get; set; }

    public Renderable BuildConsoleDisplay()
    {
        var table = new Spectre.Console.Table();
        if (Title.IsNotEmpty())
        {
            table.Title = new TableTitle(Title);
        }
        
        foreach (var column in Columns)
        {
            column.Configure(table);
        }

        foreach (var row in Rows)
        {
            var cells = row.BuildCells();
            table.AddRow(cells);
        }

        return table;
    }

    public List<TableColumn> Columns { get; } = new();
    public List<TableRow> Rows { get; } = new();

    public Table AddColumn(string key, string? header = null, Justify textAlign = Justify.Left,
        Justify headerAlign = Justify.Center, HighlightMode highlight = HighlightMode.None)
    {
        // TODO -- validate on uniqueness of the key
        var column = new TableColumn(key, header ?? key)
        {
            HeaderAlign = headerAlign,
            TextAlign = textAlign,
            Highlight = highlight
        };

        Columns.Add(column);

        return this;
    }

    public Table AddRow(params object[] values)
    {
        if (values.Length > Columns.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "More values than columns");
        }

        var row = StartRow();
        for (var i = 0; i < values.Length; i++)
        {
            var column = Columns[i];
            var raw = values[i];
            var data = raw == null ? string.Empty : column.Formatter(raw);
            row.Cells[column.Key] = new TableCell(column, data);
        }

        return this;
    }

    public TableRow StartRow()
    {
        var row = new TableRow(this);
        Rows.Add(row);
        return row;
    }

    public class TableRow
    {
        private readonly Table _parent;

        public TableRow(Table parent)
        {
            _parent = parent;
        }

        public Dictionary<string, TableCell> Cells { get; } = new();

        public TableRow With(string key, object value, Justify? align = null, HighlightMode? highlight = null)
        {
            var column = _parent.Columns.FirstOrDefault(x => x.Key == key);
            if (column == null)
            {
                column = new TableColumn(key);
                _parent.Columns.Add(column);
            }

            var data = value is null ? string.Empty : column.Formatter(value);

            if (!Cells.TryGetValue(key, out var cell))
            {
                cell = new TableCell(column, data);
                Cells[key] = cell;
            }

            cell.Text = data;

            if (align.HasValue)
            {
                cell.TextAlign = align.Value;
            }

            if (highlight.HasValue)
            {
                cell.Highlight = highlight.Value;
            }

            return this;
        }

        public Renderable[] BuildCells()
        {
            return buildCells().ToArray();
        }

        private IEnumerable<Renderable> buildCells()
        {
            foreach (var column in _parent.Columns)
            {
                if (Cells.TryGetValue(column.Key, out var cell))
                {
                    yield return cell.BuildConsoleDisplay(column);
                }
                else
                {
                    yield return new Markup("");
                }
            }
        }
    }

    public class TableCell : Fragment
    {
        public TableCell(TableColumn parent, string text)
        {
            Key = parent.Key;
            Text = text ?? string.Empty;
            TextAlign = parent.TextAlign;
            Highlight = parent.Highlight;
        }

        public string Key { get; }

        public string Text { get; set; }
        public Renderable BuildConsoleDisplay(TableColumn column)
        {
            return WrapText(Text);
        }
    }
}

public class TableColumn : Fragment
{
    public TableColumn(string header)
    {
        Key = Header = header;
    }

    public TableColumn(string key, string header)
    {
        Header = header;
        Key = key;
    }

    public Func<object, string> Formatter { get; set; } = x => x.ToString()!;
    public string Header { get; }
    public Justify HeaderAlign { get; set; } = Justify.Center;
    public string Key { get; }

    public void Configure(Spectre.Console.Table table)
    {
        var tableColumn = new Spectre.Console.TableColumn(Header);
        tableColumn.Alignment = TextAlign;
        
        table.AddColumn(tableColumn);
    }

}