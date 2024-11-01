﻿using System.Reflection;
using JasperFx.CommandLine.Internal;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JasperFx.CommandLine;

public static class HostedCommandExtensions
{
    /// <summary>
    ///     Execute the extended Oakton command line support for your configured IHost.
    ///     This method would be called within the Task&lt;int&gt; Program.Main(string[] args) method
    ///     of your AspNetCore application. This usage is appropriate for WebApplication bootstrapping.
    /// </summary>
    /// <param name="host">An already built IHost</param>
    /// <param name="args"></param>
    /// <returns></returns>
    public static async Task<int> RunJasperFxCommands(this IHost host, string[] args)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var options = scope.ServiceProvider.GetRequiredService<IOptions<JasperFxOptions>>().Value;
            args = ApplyArgumentDefaults(args, options);

            var executor = scope.ServiceProvider.GetRequiredService<CommandExecutor>();

            if (executor.Factory is CommandFactory factory)
            {
                var originalConfigureRun = factory.ConfigureRun;
                factory.ConfigureRun = cmd =>
                {
                    if (cmd.Input is IHostBuilderInput i)
                    {
                        i.HostBuilder = new PreBuiltHostBuilder(host);
                    }

                    originalConfigureRun?.Invoke(cmd);
                };
            }

            return await executor.ExecuteAsync(args);
        }
        finally
        {
            host.SafeDispose();
        }
    }

    private static string[] ApplyArgumentDefaults(string[] args, JasperFxOptions options)
    {
        // Workaround for IISExpress / VS2019 erroneously putting crap arguments
        args = args.FilterLauncherArgs();

        // Gotta apply the options file here before the magic "run" gets in
        if (options.OptionsFile.IsNotEmpty())
        {
            args = CommandExecutor.ReadOptions(options.OptionsFile).Concat(args).ToArray();
        }

        if (args == null || args.Length == 0 || args[0].StartsWith('-'))
        {
            args = new[] { options.DefaultCommand }.Concat(args ?? Array.Empty<string>()).ToArray();
        }

        return args;
    }
}