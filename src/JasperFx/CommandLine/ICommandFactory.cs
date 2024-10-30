using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.CommandLine;

/// <summary>
///     Interface that JasperFx uses to build command runs during execution. Can be used for custom
///     command activation
/// </summary>
public interface ICommandFactory
{
    CommandRun BuildRun(string commandLine);
    CommandRun BuildRun(IEnumerable<string> args);
    void RegisterCommands(Assembly assembly);

    IEnumerable<IJasperFxCommand> BuildAllCommands();

    void ApplyExtensions(IHostBuilder builder);
}