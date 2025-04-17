using JasperFx.Core.Descriptors;
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
        foreach (var property in description.Properties)
        {
            tree.AddNode($"[blue]{property.Name.EscapeMarkup()}[/]: {property.Value}");
        }

        foreach (var child in description.Children)
        {
            var node = tree.AddNode(child.Key);
            foreach (var property in child.Value.Properties)
            {
                node.AddNode($"[blue]{property.Name.EscapeMarkup()}[/]: {property.Value}");
            }
        }
        
        // if (description.Properties.Any())
        // {
        //     var table = new Table
        //     {
        //         ShowHeaders = false,
        //         Border = new NoTableBorder()
        //     };
        //     
        //     table.AddColumn(new TableColumn("Name").RightAligned());
        //     table.AddColumn(new TableColumn("Value").LeftAligned());
        //
        //     foreach (var property in description.Properties)
        //     {
        //         table.AddRow(property.Name, property.Value);
        //     }
        //     
        //     AnsiConsole.Write(table);
        //
        //     tree.AddNode(table);
        //
        // }
        
        AnsiConsole.Write(tree);
    }
}