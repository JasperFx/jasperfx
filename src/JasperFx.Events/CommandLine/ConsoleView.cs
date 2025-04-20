using JasperFx.Core;
using JasperFx.Events.Descriptors;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

internal class ConsoleView: IConsoleView
{
    public void DisplayNoStoresMessage()
    {
        AnsiConsole.Markup("[gray]No document stores in this application.[/]");
    }

    public void ListShards(EventStoreUsage[] usages)
    {
        var tree = new Tree("Projections and Subscriptions");
        
        foreach (var usage in usages)
        {
            var storeNode = tree.AddNode(usage.SubjectUri.ToString());
            if (usage.Subscriptions.Any())
            {
                var table = new Table();
                table.AddColumn("Name");
                table.AddColumn(new TableColumn("Version").RightAligned());
                table.AddColumn("Type");
                table.AddColumn("Shards");
                table.AddColumn("Lifecycle");

                foreach (var subscription in usage.Subscriptions.OrderBy(x => x.Name))
                {
                    var shards = subscription.ShardNames.Select(x => x.Identity).Join(", ");
                    table.AddRow(subscription.Name, subscription.Version.ToString(),
                        subscription.SubscriptionType.ToString(), shards, subscription.Lifecycle.ToString());
                }

                storeNode.AddNode(table);
            }
            else
            {
                storeNode.AddNode("[gray]No projections in this store.[/]");
            }
        }

        AnsiConsole.Render(tree);
        AnsiConsole.WriteLine();
    }

    public void DisplayEmptyEventsMessage(EventStoreUsage usage)
    {
        AnsiConsole.MarkupLine("[bold]The event storage is empty, aborting.[/]");
    }

    public void DisplayRebuildIsComplete()
    {
        AnsiConsole.Markup("[green]Projection Rebuild complete![/]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
    }

    public void DisplayInvalidShardTimeoutValue()
    {
        AnsiConsole.Markup("[red]Invalid Shard Timeout.[/]");
    }

    public string[] SelectStores(string[] storeNames)
    {
        return AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
            .Title("Choose document stores")
            .AddChoices(storeNames)).ToArray();
    }

    public string[] SelectProjections(string[] projectionNames)
    {
        return AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
            .Title("Choose projections")
            .AddChoices(projectionNames)).ToArray();
    }

    public void DisplayNoMatchingProjections()
    {
        AnsiConsole.Markup("[gray]No projections match the criteria.[/]");
        AnsiConsole.WriteLine();
    }

    public void WriteHeader(EventStoreUsage usage)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold blue]{usage.SubjectUri}[/]") { Justification = Justify.Left });
        AnsiConsole.WriteLine();
    }

    public void DisplayNoDatabases()
    {
        AnsiConsole.Markup("[gray]No named databases match the criteria.[/]");
        AnsiConsole.WriteLine();
    }

    public void DisplayNoAsyncProjections()
    {
        AnsiConsole.Markup("[gray]No asynchronous projections match the criteria.[/]");
        AnsiConsole.WriteLine();
    }

    public void WriteHeader(IEventDatabase database)
    {
        AnsiConsole.Write(new Rule($"Database: {database.Identifier}") { Justification = Justify.Left });
    }

    public string[] SelectDatabases(string[] databaseNames)
    {
        return AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
            .Title("Choose databases")
            .AddChoices(databaseNames)).ToArray();
    }
}
