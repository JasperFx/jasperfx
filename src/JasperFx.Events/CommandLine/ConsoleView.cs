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

    public void ListShards(IReadOnlyList<EventStoreUsage> usages)
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

    public void DisplayEmptyEventsMessage(EventStoreDatabaseIdentifier usage)
    {
        AnsiConsole.MarkupLine($"[bold]The event storage for {usage.SubjectUri}, database {usage.DatabaseIdentifier} is empty, aborting.[/]");
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

    public void WriteStartingToRebuildProjections(ProjectionSelection selection, string databaseName)
    {
        AnsiConsole.WriteLine($"Starting to rebuild projections {selection.Subscriptions.Select(x => x.Name).Join(", ")} ");
    }

    public void DisplayNoAsyncProjections()
    {
        AnsiConsole.Markup("[gray]No asynchronous projections match the criteria.[/]");
        AnsiConsole.WriteLine();
    }

}
