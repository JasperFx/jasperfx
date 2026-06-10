using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Events.Descriptors;
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
            if (input.Action == ProjectionAction.rebuild)
            {
                return await ExecuteRebuilds(input, selection, shardTimeout);
            }

            await RunContinuously(selections);
        }

        return true;
    }

    public async Task<bool> ExecuteRebuilds(ProjectionInput input, ProjectionSelection selection,
        TimeSpan? shardTimeout)
    {
        var stopwatch = Stopwatch.StartNew();
        
        foreach (var database in selection.DatabaseIdentifiers)
        {
            _view.WriteStartingToRebuildProjections(selection, database);

            try
            {
                // #4711: a Live-lifecycle projection has no persisted state, so there is nothing to
                // rebuild — and replaying it across the whole event store runs its aggregation over
                // unrelated streams (which lack its events) and throws. Exclude Live here so both
                // rebuild-all and an explicitly named Live projection are skipped. Inline and Async
                // both have stored state and remain rebuildable.
                //
                // Also exclude pure subscriptions (SubscriptionType.Subscription, e.g. Wolverine's
                // PublishEventsToWolverine): a subscription has no persisted projected state to rebuild,
                // it is a forward-only event relay, and the daemon's RebuildProjectionAsync only resolves
                // PROJECTION names (TryFindProjection searches the projection list, not subscriptions), so
                // feeding a subscription name into a rebuild throws "No registered projection matches...".
                // Rebuilding a subscription would also re-publish every historical event — never desired,
                // and directly contrary to a SubscribeFromPresent() registration.
                var subscriptionNames = selection.Subscriptions
                    .Where(x => x.Lifecycle != ProjectionLifecycle.Live)
                    .Where(x => x.SubscriptionType != SubscriptionType.Subscription)
                    .Select(x => x.Name).ToArray();
                var databaseIdentifier = new EventStoreDatabaseIdentifier(selection.Storage.SubjectUri, database);
                var status = await _host.TryRebuildShardsAsync(databaseIdentifier, input, subscriptionNames ,shardTimeout).ConfigureAwait(false);

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
