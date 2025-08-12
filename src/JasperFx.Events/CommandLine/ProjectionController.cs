using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

public class ProjectionController
{
    private readonly IProjectionHost _host;
    private readonly IConsoleView _view;

    public ProjectionController(IProjectionHost host, IConsoleView view)
    {
        _host = host;
        _view = view;
    }

    public async Task<bool> Execute(ProjectionInput input)
    {
        TimeSpan? shardTimeout = null;
        try
        {
            shardTimeout = !string.IsNullOrEmpty(input.ShardTimeoutFlag)
                ? input.ShardTimeoutFlag.ToTime()
                : null;
        }
        catch (Exception)
        {
            _view.DisplayInvalidShardTimeoutValue();
            return false;
        }

        var usages = await _host.AllStoresAsync();

        if (!usages.Any())
        {
            _view.DisplayNoStoresMessage();
            return true;
        }

        if (input.Action == ProjectionAction.list)
        {
            _view.ListShards(usages);
            return true;
        }


        var selections = ProjectionSelection.Filter(usages, input);
        if (!selections.Any())
        {
            AnsiConsole.MarkupLine("[bold]No projections or databases match the selected filters[/]");
            return true;
        }

        if (input.Action == ProjectionAction.rebuild)
        {
            _host.ListenForUserTriggeredExit();
        }

        foreach (var selection in selections)
        {
            if (input.AdvanceFlag)
            {
                await _host.AdvanceHighWaterMarkToLatestAsync(selection, CancellationToken.None).ConfigureAwait(false);
            }

            if (input.Action == ProjectionAction.rebuild)
            {
                return await ExecuteRebuilds(selection, shardTimeout);
            }

            await RunContinuously(selections);
        }

        return true;
    }

    public async Task<bool> ExecuteRebuilds(ProjectionSelection selection, TimeSpan? shardTimeout)
    {
        var stopwatch = Stopwatch.StartNew();
        
        foreach (var database in selection.DatabaseIdentifiers)
        {
            _view.WriteStartingToRebuildProjections(selection, database);

            try
            {
                var subscriptionNames = selection.Subscriptions.Select(x => x.Name).ToArray();
                var databaseIdentifier = new EventStoreDatabaseIdentifier(selection.Storage.SubjectUri, database);
                var status = await _host.TryRebuildShardsAsync(databaseIdentifier, subscriptionNames ,shardTimeout).ConfigureAwait(false);

                if (status == RebuildStatus.NoData)
                {
                    _view.DisplayEmptyEventsMessage(databaseIdentifier);
                }
                else
                {
                    _view.DisplayRebuildIsComplete();
                    AnsiConsole.WriteLine($"[purple]Finished rebuild in {stopwatch.ElapsedMilliseconds} ms[/]");
                }
            }
            catch (Exception)
            {
                AnsiConsole.MarkupLine("[red]Errors detected[/]");

                return false;
            }
        }

        return true;
    }

    private async Task RunContinuously(IReadOnlyList<ProjectionSelection> selections)
    {
        AnsiConsole.Clear();
        selections = ProjectionSelection.FilterForAsyncOnly(selections);
        if (!selections.Any())
        {
            _view.DisplayNoAsyncProjections();
            return;
        }
    
        foreach (var selection in selections)
        {
            var projectionNames = selection.Subscriptions.Select(x => x.Name).ToArray();
            
            foreach (var databaseName in selection.DatabaseIdentifiers)
            {
                await _host.StartShardsAsync(new EventStoreDatabaseIdentifier(selection.Storage.SubjectUri, databaseName), projectionNames);
            }
        }

        await _host.WaitForExitAsync().ConfigureAwait(false);
    }
}
