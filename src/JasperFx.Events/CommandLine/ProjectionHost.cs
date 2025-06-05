using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using ImTools;
using JasperFx.Core;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

internal class ProjectionHost: IProjectionHost
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IHost _host;
    private readonly Lazy<DaemonStatusGrid> _statusGrid;
    private ImHashMap<Uri, IEventStore> _stores = ImHashMap<Uri, IEventStore>.Empty;

    public ProjectionHost(IHost host)
    {
        _host = host;

        _statusGrid = new Lazy<DaemonStatusGrid>(() =>
        {
            return new DaemonStatusGrid();
        });
    }

    public async Task<IReadOnlyList<EventStoreUsage>> AllStoresAsync()
    {
        var stores = _host.Services.GetServices<IEventStore>();
        var list = new List<EventStoreUsage>();

        foreach (var store in stores)
        {
            _stores = _stores.AddOrUpdate(store.Subject, store);
            var usage = await store.TryCreateUsage(CancellationToken.None);
            if (usage != null)
            {
                list.Add(usage);
            }
        }

        return list;
    }

    public void ListenForUserTriggeredExit()
    {
        var assembly = Assembly.GetEntryAssembly()!;
        AssemblyLoadContext.GetLoadContext(assembly)!.Unloading += context => Shutdown();

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            Shutdown();
            eventArgs.Cancel = true;
        };

        var shutdownMessage = "Press CTRL + C to quit";
        Console.WriteLine(shutdownMessage);
    }

    public void Shutdown()
    {
        _cancellation.Cancel();
        _completion.TrySetResult(true);
    }

    public async Task<RebuildStatus> TryRebuildShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier,
        string[] projectionNames, TimeSpan? shardTimeout = null)
    {
        if (!_stores.TryFind(databaseIdentifier.SubjectUri, out var store))
        {
            throw new ArgumentOutOfRangeException(nameof(databaseIdentifier));
        }

        using var daemon = await store.BuildProjectionDaemonAsync(databaseIdentifier.DatabaseIdentifier);
        await daemon.PrepareForRebuildsAsync().ConfigureAwait(false);

        var highWater = daemon.Tracker.HighWaterMark;
        if (highWater == 0)
        {
            return RebuildStatus.NoData;
        }

        var watcher = new RebuildWatcher(highWater);
        using var unsubscribe = daemon.Tracker.Subscribe(watcher);

        var watcherTask = watcher.Start();

        var list = new List<Exception>();

        await Parallel.ForEachAsync(projectionNames, _cancellation.Token,
                async (projectionName, token) =>
                {
                    shardTimeout ??= 5.Minutes();

                    try
                    {
                        var stopwatch = new Stopwatch();
                        stopwatch.Start();
                        await daemon.RebuildProjectionAsync(projectionName, shardTimeout.Value, token).ConfigureAwait(false);
                        AnsiConsole.MarkupLine($"[green]Finished rebuilding {projectionName} in {stopwatch.ElapsedMilliseconds} ms[/]");
                    }
                    catch (Exception e)
                    {
                        AnsiConsole.MarkupLine($"[bold red]Error while rebuilding projection {projectionName} on database '{databaseIdentifier.DatabaseIdentifier}'[/]");
                        AnsiConsole.WriteException(e);
                        AnsiConsole.WriteLine();

                        list.Add(e);
                    }
                })
            .ConfigureAwait(false);

        await daemon.StopAllAsync().ConfigureAwait(false);

        watcher.Stop();
        await watcherTask.ConfigureAwait(false);

        if (list.Any())
        {
            throw new AggregateException(list);
        }

        return RebuildStatus.Complete;
    }

    public async Task StartShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier, string[] projectionNames)
    {
        if (!_stores.TryFind(databaseIdentifier.SubjectUri, out var store))
        {
            throw new ArgumentOutOfRangeException(nameof(databaseIdentifier));
        }

        using var daemon = await store.BuildProjectionDaemonAsync(databaseIdentifier.DatabaseIdentifier);
        var watcher = new DaemonWatcher(databaseIdentifier.SubjectUri, databaseIdentifier.DatabaseIdentifier, _statusGrid.Value);

        daemon.Tracker.Subscribe(watcher);

        foreach (var projectionName in projectionNames)
        {
            await daemon.StartAgentAsync(projectionName, _cancellation.Token).ConfigureAwait(false);
        }
    }

    public Task WaitForExitAsync()
    {
        return _completion.Task;
    }

    public Task AdvanceHighWaterMarkToLatestAsync(ProjectionSelection selection, CancellationToken none)
    {
        throw new NotImplementedException();
    }
}
