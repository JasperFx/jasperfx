using JasperFx.Core;
using JasperFx.Events.Projections;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

internal class StoreDaemonStatus
{
    public readonly LightweightCache<string, DatabaseStatus> Databases 
        = new(subject => new DatabaseStatus(subject));

    public StoreDaemonStatus(Uri subject)
    {
        Subject = subject;
    }

    public Uri Subject { get; }

    public void ReadState(string databaseName, ShardState state)
    {
        Databases[databaseName].ReadState(state);
    }

    public Table BuildTable()
    {
        var table = new Table { Title = new TableTitle(Subject.ToString(), Style.Parse("bold")) };
        var databases = Databases.OrderBy(x => x.Name).ToArray();
        if (databases.Length == 1)
        {
            var database = databases.Single();
            return BuildTableForSingleDatabase(table, database);
        }

        return BuildTableForMultipleDatabases(table, databases);
    }

    private static Table BuildTableForMultipleDatabases(Table table, DatabaseStatus[] databases)
    {
        table.AddColumns("Database", "Shard", "Sequence", "Status");
        table.Columns[2].Alignment = Justify.Right;
        foreach (var database in databases)
        {
            table.AddRow(new Markup($"[blue]{database.Name}[/]"), new Markup("[blue]High Water Mark[/]"),
                new Markup($"[blue]{database.HighWaterMark}[/]"), new Markup("[gray]Active[/]"));

            foreach (var shard in database.Shards.OrderBy(x => x.ShardName))
            {
                table.AddRow(database.Name, shard.ShardName, shard.Sequence.ToString(), shard.State.ToString());
            }
        }

        return table;
    }

    private static Table BuildTableForSingleDatabase(Table table, DatabaseStatus database)
    {
        table.AddColumns("Shard", "Sequence", "Status");
        table.Columns[1].Alignment = Justify.Right;
        table.AddRow(new Markup("[blue]High Water Mark[/]"), new Markup($"[blue]{database.HighWaterMark}[/]"),
            new Markup("[gray]Active[/]"));

        foreach (var shard in database.Shards.OrderBy(x => x.ShardName))
        {
            table.AddRow(shard.ShardName, shard.Sequence.ToString(), shard.State.ToString());
        }

        return table;
    }
}
