using JasperFx.Descriptors;
using Spectre.Console;
using Spectre.Console.Rendering;
using Table = Spectre.Console.Table;
using TableColumn = Spectre.Console.TableColumn;
using Tree = Spectre.Console.Tree;


namespace JasperFx.CommandLine;

public static class OptionDescriptionWriter
{
    public static void Write(OptionsDescription description)
    {
        var tree = new Tree(description.Subject);
        tree.WriteProperties(description);
        
        tree.WriteChildren(description);
        foreach (var pair in description.Sets.OrderBy(x => x.Key))
        {
            var setNode = tree.AddNode($"[blue]{pair.Key.EscapeMarkup()}[/]");
            setNode.WriteOptionSet(pair.Value);
        }
        
        AnsiConsole.Write(tree);
    }

    public static void WriteProperties(this IHasTreeNodes parent, OptionsDescription description)
    {
        foreach (var property in description.Properties.OrderBy(x => x.Name))
        {
            parent.AddNode($"[blue]{property.Name.EscapeMarkup()}[/]: {property.Value.EscapeMarkup()}");
        }
    }

    public static void WriteChildren(this IHasTreeNodes parent, OptionsDescription description)
    {
        foreach (var child in description.Children.OrderBy(x => x.Key))
        {
            var node = parent.AddNode($"[blue]{child.Key.EscapeMarkup()}[/]");
            node.WriteProperties(child.Value);
            node.WriteChildren(child.Value);

            foreach (var pair in child.Value.Sets.OrderBy(x => x.Key))
            {
                var setNode = node.AddNode($"[blue]{pair.Key.EscapeMarkup()}[/]");
                setNode.WriteOptionSet(pair.Value);
            }
        }
    }

    public static void WriteOptionSet(this IHasTreeNodes parent, OptionSet set)
    {
        if (set.RenderMode == SetRenderMode.Table)
        {
            writeOptionSetAsTable(parent, set);
        }
        else
        {
            writeOptionSetAsTree(parent, set);
        }
    }

    private static void writeOptionSetAsTree(IHasTreeNodes parent, OptionSet set)
    {
        parent = parent.AddNode(set.Subject);
        
        foreach (var child in set.Rows.OrderBy(x => x.Title))
        {
            var node = parent.AddNode($"[blue]{child.Title.EscapeMarkup()}[/]");
            node.WriteProperties(child);
            node.WriteChildren(child);

            foreach (var pair in child.Sets.OrderBy(x => x.Key))
            {
                var setNode = node.AddNode($"[blue]{pair.Key.EscapeMarkup()}[/]");
                setNode.WriteOptionSet(pair.Value);
            }
        }
    }

    private static void writeOptionSetAsTable(IHasTreeNodes parent, OptionSet set)
    {
        var columns = set.SummaryColumns;
        if (!columns.Any())
        {
            columns = set.Rows.SelectMany(x => x.Properties).Select(x => x.Name).Distinct().ToArray();
        }
        
        var table = new Table();
        foreach (var column in columns)
        {
            table.AddColumn(column);
        }

        foreach (var description in set.Rows)
        {
            var values = columns.Select(column =>
            {
                var prop = description.Properties.FirstOrDefault(x => x.Name == column);
                return prop?.Value.EscapeMarkup() ?? string.Empty;
            }).ToArray();

            table.AddRow(values);
        }

        parent.AddNode(table);
    }
}