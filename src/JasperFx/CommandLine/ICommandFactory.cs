using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.CommandLine;

/// <summary>
///     Interface that JasperFx uses to build command runs during execution. Can be used for custom
///     command activation.
/// </summary>
/// <remarks>
///     The annotations propagate the reflective surface to implementations (default
///     <see cref="CommandFactory"/>) and to consumers (<see cref="CommandExecutor"/>,
///     <see cref="CommandLineHostingExtensions"/>). AOT/trim-clean apps bypass
///     the reflective discovery path via <see cref="CommandFactory.TryRegisterCommandsFromManifest"/>
///     and the JasperFx.SourceGenerator-emitted <c>DiscoveredCommands</c> manifest.
/// </remarks>
public interface ICommandFactory
{
    [RequiresUnreferencedCode("BuildRun resolves command types reflectively via ICommandCreator; their public constructors and input-type properties must survive trimming.")]
    [RequiresDynamicCode("Command input parsing closes generic List<T> via MakeGenericType for enumerable arguments / flags.")]
    CommandRun BuildRun(string commandLine);

    [RequiresUnreferencedCode("BuildRun resolves command types reflectively via ICommandCreator; their public constructors and input-type properties must survive trimming.")]
    [RequiresDynamicCode("Command input parsing closes generic List<T> via MakeGenericType for enumerable arguments / flags.")]
    CommandRun BuildRun(IEnumerable<string> args);

    [RequiresUnreferencedCode("Scans assembly.GetExportedTypes() for IJasperFxCommand. AOT-publishing apps should rely on the source-generated DiscoveredCommands manifest emitted by JasperFx.SourceGenerator.")]
    void RegisterCommands(Assembly assembly);

    [RequiresUnreferencedCode("BuildAllCommands dispatches each registered command type through ICommandCreator.CreateCommand, which instantiates the type reflectively.")]
    IEnumerable<IJasperFxCommand> BuildAllCommands();

    [RequiresUnreferencedCode("Activator.CreateInstance(extensionType) requires public parameterless constructor of each extension to survive trimming.")]
    void ApplyExtensions(IHostBuilder builder);
}