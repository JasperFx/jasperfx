using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using ImTools;
using JasperFx.Core;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: CLI projection host uses Type.MakeGenericType for IEventStore<TOperations, TQuerySession> shape discovery — runtime code generation. CLI tooling is a development-time surface, not part of AOT-published runtime.")]
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
        ProjectionInput input,
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

        // jasperfx#420: cap the per-database rebuild fan-out so a wide store
        // (many projections, especially under per-tenant partitioning) cannot
        // blow the connection pool / thrash the buffer cache. The --max-concurrent
        // CLI flag wins; otherwise the store's configured default applies; otherwise
        // the previous unbounded behavior. A non-positive value stays unbounded.
        var maxConcurrent = input.ResolveMaxDegreeOfParallelism(store.MaxConcurrentRebuildsPerDatabase);

        AnsiConsole.MarkupLine(maxConcurrent > 0
            ? $"[grey]Rebuilding with a maximum of {maxConcurrent} concurrent projection(s) per database[/]"
            : "[grey]Rebuilding with unbounded concurrency per database[/]");

        // Concurrent collection: with maxConcurrent > 1 the rebuild lambda runs on
        // multiple threads, so a plain List<Exception>.Add would race/corrupt.
        var errors = new ConcurrentBag<Exception>();

        await RebuildProjectionsWithCapAsync(projectionNames, maxConcurrent, _cancellation.Token,
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

                        errors.Add(e);
                    }
                })
            .ConfigureAwait(false);

        await daemon.StopAllAsync().ConfigureAwait(false);

        watcher.Stop();
        await watcherTask.ConfigureAwait(false);

        if (!errors.IsEmpty)
        {
            throw new AggregateException(errors);
        }

        return RebuildStatus.Complete;
    }

    /// <summary>
    /// jasperfx#420 — the per-database rebuild fan-out, capped at <paramref name="maxDegreeOfParallelism"/>
    /// concurrent cells. Extracted so the cap is independently regression-testable: a non-positive value
    /// means unbounded (<see cref="ParallelOptions.MaxDegreeOfParallelism"/> = -1; 0 would throw), and a
    /// positive value guarantees no more than that many invocations of <paramref name="rebuildOne"/> run
    /// at once.
    /// </summary>
    internal static Task RebuildProjectionsWithCapAsync(
        IReadOnlyList<string> projectionNames,
        int maxDegreeOfParallelism,
        CancellationToken token,
        Func<string, CancellationToken, Task> rebuildOne)
    {
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : -1
        };

        return Parallel.ForEachAsync(projectionNames, parallelOptions,
            async (projectionName, ct) => await rebuildOne(projectionName, ct).ConfigureAwait(false));
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

    public async Task AdvanceHighWaterMarkToLatestAsync(ProjectionSelection selection, CancellationToken none)
    {
        throw new NotImplementedException();
    }
}
