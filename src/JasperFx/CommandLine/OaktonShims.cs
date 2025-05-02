


using System.Reflection;
using JasperFx;
using JasperFx.CommandLine.Commands;
using JasperFx.CommandLine.Internal;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
// ReSharper disable once CheckNamespace
using JasperFx.CommandLine;

namespace Oakton;

[Obsolete("Prefer JasperFxAsyncCommand")]
public abstract class OaktonAsyncCommand<T> : JasperFxAsyncCommand<T>
{
    
}

[Obsolete("Prefer JasperFxCommand")]
public abstract class OaktonCommand<T> : JasperFxCommand<T>{}

[Obsolete("Prefer JasperFxEnvironment")]
public static class OaktonEnvironment
{
    /// <summary>
    ///     If using Oakton as the run command in .Net Core applications with WebApplication,
    ///     this will force Oakton to automatically start up the IHost when the Program.Main()
    ///     method runs. Very useful for WebApplicationFactory testing
    /// </summary>
    public static bool AutoStartHost
    {
        get => JasperFxEnvironment.AutoStartHost;
        set => JasperFxEnvironment.AutoStartHost = value;
    }
}

public static class CommandLineHostingExtensions
{
    /// <summary>
    ///     Discover and apply Oakton extensions to this application during
    ///     bootstrapping. This is only necessary when using the WebApplication
    ///     approach to bootstrapping applications introduced in .Net 6
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    [Obsolete("Prefer ApplyJasperFxExtensions")]
    public static IHostBuilder ApplyOaktonExtensions(this IHostBuilder builder)
    {
        return builder.ApplyJasperFxExtensions();
    }

    /// <summary>
    ///     Execute the extended Oakton command line support for your configured WebHostBuilder.
    ///     This method would be called within the Task&lt;int&gt; Program.Main(string[] args) method
    ///     of your AspNetCore application
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="args"></param>
    /// <param name="optionsFile">Optionally configure an expected "opts" file</param>
    /// <returns></returns>
    [Obsolete("Prefer RunJasperFxCommands")]
    public static Task<int> RunOaktonCommands(this IHostBuilder builder, string[] args, string? optionsFile = null)
    {
        return builder.RunJasperFxCommands(args, optionsFile);
    }

    /// <summary>
    ///     Execute the extended Oakton command line support for your configured IHost.
    ///     This method would be called within the Task&lt;int&gt; Program.Main(string[] args) method
    ///     of your AspNetCore application. This usage is appropriate for WebApplication bootstrapping
    /// </summary>
    /// <param name="host">An already built IHost</param>
    /// <param name="args"></param>
    /// <param name="optionsFile">Optionally configure an expected "opts" file</param>
    /// <returns></returns>
    [Obsolete("Prefer RunJasperFxCommands")]
    public static Task<int> RunOaktonCommands(this IHost host, string[] args, string? optionsFile = null)
    {
        return host.RunJasperFxCommands(args, optionsFile);
    }
}