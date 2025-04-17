using JasperFx.Core.Descriptions;
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
            tree.AddNode($"{property.Name} = {property.Value}");
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