using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace JasperFx.CommandLine.Help;

[Description("List all the available commands", Name = "help")]
public class HelpCommand : JasperFxCommand<HelpInput>
{
    public HelpCommand()
    {
        Usage("List all the available commands").Arguments(x => x.Name);
        Usage("Show all the valid usages for a command");
    }

    public override bool Execute(HelpInput input)
    {
        if (input.JsonFlag)
        {
            // Machine-readable command catalog for tooling (e.g. the JasperFx.Aspire dashboard
            // integration discovering verbs). Pure introspection — no host is built.
            Console.WriteLine(ToCommandCatalogJson(input.CommandTypes));
            return true;
        }

        if (input.Usage != null)
        {
            input.Usage.WriteUsages(input.AppName);
            return false;
        }

        if (input.InvalidCommandName)
        {
            writeInvalidCommand(input.Name);
            listAllCommands(input);
            return false;
        }

        listAllCommands(input);
        return true;
    }

    private void listAllCommands(HelpInput input)
    {
        if (!input.CommandTypes.Any())
        {
            Console.WriteLine("There are no known commands in this executable!");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]The available commands are:[/]");

        var table = new Table
        {
            Border = TableBorder.SimpleHeavy
        };

        table.AddColumns("Alias", "Description");
        foreach (var type in input.CommandTypes.OrderBy(CommandFactory.CommandNameFor))
            table.AddRow(CommandFactory.CommandNameFor(type), CommandFactory.DescriptionFor(type));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "Use [italic]dotnet run -- ? [[command name]][/] or [italic]dotnet run -- help [[command name]][/] to see usage help about a specific command");
    }

    private void writeInvalidCommand(string commandName)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red]'{commandName}' is not a command.  See available commands.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Render the command catalog as a JSON array of <c>{ "name", "description" }</c> objects, sorted
    /// by command name. Written with <see cref="Utf8JsonWriter"/> (no reflection) so it is safe under
    /// trimming / AOT.
    /// </summary>
    public static string ToCommandCatalogJson(IEnumerable<Type> commandTypes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var type in commandTypes.OrderBy(CommandFactory.CommandNameFor))
            {
                writer.WriteStartObject();
                writer.WriteString("name", CommandFactory.CommandNameFor(type));
                writer.WriteString("description", CommandFactory.DescriptionFor(type));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}