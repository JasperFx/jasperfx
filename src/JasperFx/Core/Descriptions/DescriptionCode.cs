namespace JasperFx.Core.Descriptions;

/*
 * TODOs
 * Everything needs to be serializable as JSON
 * Markdown writer
 * HtmlTag equivalent???
 * Write MarkUp for spectre
 *
 *
 *
 * 
 */

public interface IRenderable{};

public enum HighlightMode
{
    Success,
    Fail,
    Warning,
    Italic,
    Bold,
    BoldedItalic,
    None
}

public abstract class Fragment
{
    public bool Italic { get; set; }
    public bool Bold { get; set; }
    public HighlightMode Highlight { get; set; } = HighlightMode.None;
    public TextAlign TextAlign { get; set; } = TextAlign.Left;
}

public class Span : Fragment, IRenderable
{
    public Span(string text)
    {
        Text = text;
    }

    public string Text { get; set; }
}

public class Line : Fragment, IRenderable
{
    public Line(string text)
    {
        Text = text;
    }

    public string Text { get; set; }
}

public class Sentence : Fragment, IRenderable
{
    public List<Span> Spans { get; } = new();
}

public enum TextAlign
{
    Left,
    Right,
    Center
}



public class Table : IRenderable
{
    public string? Title { get; set; }

    public Table WithColumn(string key, string? header = null, TextAlign textAlign = TextAlign.Left,
        TextAlign headerAlign = TextAlign.Center, HighlightMode highlight = HighlightMode.None)
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
    
    public List<TableColumn> Columns { get; } = new();
    public List<TableRow> Rows { get; } = new();

    public Table WithRow(params object[] values)
    {
        if (values.Length > Columns.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(values),"More values than columns");
        }

        var row = StartRow();
        for (int i = 0; i < values.Length; i++)
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
        
        public TableRow With(string key, object value, TextAlign? align = null, HighlightMode? highlight = null)
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

    public Func<object, string> Formatter { get; set; } = x => x.ToString();
    public TextAlign TextAlign { get; set; } = TextAlign.Left;
    public string Header { get; }
    public TextAlign HeaderAlign { get; set; } = TextAlign.Center;
    public string Key { get; private set; }
}