using System.Runtime.Loader;
using JasperFx.Core;
using JasperFx.Environment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.CommandLine.Commands;

public class RunInput : NetCoreInput
{

    [Description("Run the environment checks before starting the host")]
    public bool CheckFlag { get; set; }
}

[Description("Start and run this .Net application")]
public class RunCommand : JasperFxAsyncCommand<RunInput>
{
    public override async Task<bool> Execute(RunInput input)
    {
        using var host = input.BuildHost();
        
        if (input.CheckFlag)
        {
            var checks = await EnvironmentChecker.ExecuteAllEnvironmentChecks(host.Services);
            checks.Assert();
        }

        // Await shutdown rather than block on it. A blocking wait here parks a thread-pool thread
        // for the life of the app, and in a container limited to under one CPU that is the pool's
        // only minimum thread, so every burst of work waits ~500ms for the pool to add one (#1092).
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // ReSharper disable once PossibleNullReferenceException
        AssemblyLoadContext.GetLoadContext(typeof(RunCommand).Assembly)!.Unloading +=
            (Action<AssemblyLoadContext>)(context => stopped.TrySetResult());
        Console.CancelKeyPress += (ConsoleCancelEventHandler)((sender, eventArgs) =>
        {
            stopped.TrySetResult();
            eventArgs.Cancel = true;
        });

        var lifetime = host.Services.GetService<IHostApplicationLifetime>();
        lifetime?.ApplicationStopping.Register(() => stopped.TrySetResult());

        // A host started under JasperFxEnvironment.AutoStartHost (WebApplicationFactory testing)
        // has already run every IHostedService.StartAsync. IHost.StartAsync is not re-entrant, so
        // starting it again re-runs them all — skip the redundant start.
        if (lifetime is null || !lifetime.ApplicationStarted.IsCancellationRequested)
        {
            await host.StartAsync();
        }

        await stopped.Task;
        await host.StopAsync();
        return true;
    }
}
