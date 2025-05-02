using System.Runtime.Loader;
using JasperFx.Core;
using JasperFx.Environment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.CommandLine.Commands;

/*
run [-a|--arch <ARCHITECTURE>] [-c|--configuration <CONFIGURATION>]

   [-f|--framework <FRAMEWORK>] [--force] [--interactive]
   [--launch-profile <NAME>] [--no-build]
   [--no-dependencies] [--no-launch-profile] [--no-restore]
   [--os <OS>] [--project <PATH>] [-r|--runtime <RUNTIME_IDENTIFIER>]
   [--tl:[auto|on|off]] [-v|--verbosity <LEVEL>]
   [[--] [application arguments]]
 */


public class RunInput : NetCoreInput
{
    private const string CannotBeNullOrEmptyAndMustBeInTheFormKeyValue = "Cannot be null or empty and must be in the form KEY=VALUE";
    
    [Description("Run the environment checks before starting the host")]
    public bool CheckFlag { get; set; }


    [Description("Value in the form <KEY=VALUE> to set an environment variable for this process")]
    public string? EnvironmentFlag
    {
        set
        {
            if (value.IsEmpty() || !value.Contains('='))
            {
                throw new ArgumentOutOfRangeException(nameof(EnvironmentFlag), CannotBeNullOrEmptyAndMustBeInTheFormKeyValue);
            }
            
            var parts = value.Split('=');
            if (parts.Length != 2)
            {
                throw new ArgumentOutOfRangeException(nameof(EnvironmentFlag), CannotBeNullOrEmptyAndMustBeInTheFormKeyValue);
            }

            if (parts.Any(x => x.IsEmpty()))
            {
                throw new ArgumentOutOfRangeException(nameof(EnvironmentFlag), CannotBeNullOrEmptyAndMustBeInTheFormKeyValue);
            }
            
            System.Environment.SetEnvironmentVariable(parts[0], parts[1]);
        }
    }

    public void ApplyEnvironmentVariablesIfAny()
    {
        
    }
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

        var reset = new ManualResetEventSlim();
        // ReSharper disable once PossibleNullReferenceException
        AssemblyLoadContext.GetLoadContext(typeof(RunCommand).Assembly)!.Unloading +=
            (Action<AssemblyLoadContext>)(context => reset.Set());
        Console.CancelKeyPress += (ConsoleCancelEventHandler)((sender, eventArgs) =>
        {
            reset.Set();
            eventArgs.Cancel = true;
        });

        var lifetime = host.Services.GetService<IHostApplicationLifetime>();
        lifetime?.ApplicationStopping.Register(() => reset.Set());

        await host.StartAsync();
        reset.Wait();
        await host.StopAsync();
        return true;
    }
}
