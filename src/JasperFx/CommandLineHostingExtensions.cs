using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Commands;
using JasperFx.CommandLine.Internal;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JasperFx;

public static class CommandLineHostingExtensions
{
    // jasperfx#1141. These annotations are static, so they fire whether or not
    // JasperFx.SourceGenerator is wired in; the messages say when it is safe to suppress them.
    // The JasperFx.AotCliSmoke gate (./build.sh smoke-test-aot-cli) publishes an app that does
    // exactly that with Native AOT and runs its commands.
    internal const string ApplyExtensionsTrimMessage =
        "Finds [JasperFxAssembly] extension assemblies and creates their declared extension types with Activator.CreateInstance; commands come from each assembly's JasperFx.SourceGenerator registration, falling back to a reflective scan. When JasperFx.SourceGenerator ran over every assembly that contributes commands, and no extension type depends on reflection, a trimmed or Native AOT app can suppress this warning at the call site. See https://shared-libs.jasperfx.net/codegen/aot.";

    internal const string RunCommandsTrimMessage =
        "Resolves commands from the entry and extension assemblies through each assembly's JasperFx.SourceGenerator registration, falling back to a reflective scan and Activator.CreateInstance for any assembly built without it. When JasperFx.SourceGenerator ran over every assembly that contributes commands, a trimmed or Native AOT app can suppress this warning at the call site. See https://shared-libs.jasperfx.net/codegen/aot.";

    internal const string RunCommandsDynamicCodeMessage =
        "Command input parsing closes List<T> with MakeGenericType for enumerable arguments and flags of an input type without a source-generated parser. JasperFx.SourceGenerator emits those parsers, so with it in place a Native AOT app can suppress this warning at the call site. See https://shared-libs.jasperfx.net/codegen/aot.";

    /// <summary>
    ///     Discover and apply JasperFx extensions to this application during
    ///     bootstrapping. This is only necessary when using the WebApplication
    ///     approach to bootstrapping applications introduced in .Net 6
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode(ApplyExtensionsTrimMessage)]
    public static IHostBuilder ApplyJasperFxExtensions(this IHostBuilder builder)
    {
        var factory = new CommandFactory();
        factory.RegisterCommandsFromExtensionAssemblies();
        factory.ApplyExtensions(builder);

        return builder;
    }

    /// <summary>
    ///     Execute the extended JasperFx command line support for your configured WebHostBuilder.
    ///     This method would be called within the Task&lt;int&gt; Program.Main(string[] args) method
    ///     of your AspNetCore application
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="args"></param>
    /// <param name="optionsFile">Optionally configure an expected "opts" file</param>
    /// <returns></returns>
    [RequiresUnreferencedCode(RunCommandsTrimMessage)]
    [RequiresDynamicCode(RunCommandsDynamicCodeMessage)]
    public static Task<int> RunJasperFxCommands(this IHostBuilder builder, string[] args, string? optionsFile = null)
    {
        return execute(builder, Assembly.GetEntryAssembly(), args, optionsFile);
    }
    
    /// <summary>
    ///     Execute the extended JasperFx command line support for your configured WebHostBuilder.
    ///     This method would be called within the int Program.Main(string[] args) method
    ///     of your AspNetCore application
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="args"></param>
    /// <param name="optionsFile">Optionally configure an expected "opts" file</param>
    /// <returns></returns>
    [RequiresUnreferencedCode(RunCommandsTrimMessage)]
    [RequiresDynamicCode(RunCommandsDynamicCodeMessage)]
    public static int RunJasperFxCommandsSynchronously(this IHostBuilder builder, string[] args, string? optionsFile = null)
    {
        return execute(builder, Assembly.GetEntryAssembly(), args, optionsFile).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Execute the extended JasperFx command line support for your configured IHost.
    ///     This method would be called within the Task&lt;int&gt; Program.Main(string[] args) method
    ///     of your AspNetCore application. This usage is appropriate for WebApplication bootstrapping
    /// </summary>
    /// <param name="host">An already built IHost</param>
    /// <param name="args"></param>
    /// <param name="optionsFile">Optionally configure an expected "opts" file</param>
    /// <returns></returns>
    [RequiresUnreferencedCode(RunCommandsTrimMessage)]
    [RequiresDynamicCode(RunCommandsDynamicCodeMessage)]
    public static Task<int> RunJasperFxCommands(this IHost host, string[] args, string? optionsFile = null)
    {
        return execute(new PreBuiltHostBuilder(host), Assembly.GetEntryAssembly(), args, optionsFile);
    }
    
    /// <summary>
    ///     Execute the extended JasperFx command line support for your configured IHost.
    ///     This method would be called within the int Program.Main(string[] args) method
    ///     of your AspNetCore application. This usage is appropriate for WebApplication bootstrapping
    /// </summary>
    /// <param name="host">An already built IHost</param>
    /// <param name="args"></param>
    /// <param name="optionsFile">Optionally configure an expected "opts" file</param>
    /// <returns></returns>
    [RequiresUnreferencedCode(RunCommandsTrimMessage)]
    [RequiresDynamicCode(RunCommandsDynamicCodeMessage)]
    public static int RunJasperFxCommandsSynchronously(this IHost host, string[] args, string? optionsFile = null)
    {
        return execute(new PreBuiltHostBuilder(host), Assembly.GetEntryAssembly(), args, optionsFile).GetAwaiter().GetResult();
    }

    internal static string[] ApplyArgumentDefaults(this string[] args, string? optionsFile)
    {
        // Workaround for IISExpress / VS2019 erroneously putting crap arguments
        args = args.FilterLauncherArgs();

        // Gotta apply the options file here before the magic "run" gets in
        if (optionsFile.IsNotEmpty())
        {
            args = CommandExecutor.ReadOptions(optionsFile).Concat(args).ToArray();
        }

        if (args == null || args.Length == 0 || args[0].StartsWith('-'))
        {
            args = new[] { "run" }.Concat(args ?? Array.Empty<string>()).ToArray();
        }

        return args;
    }

    /// <summary>
    /// Populate <paramref name="factory"/> with the standard JasperFx command set:
    /// built-in commands shipped with the framework, the application's own commands,
    /// and commands contributed by extension assemblies.
    /// </summary>
    /// <remarks>
    /// Each <see cref="CommandFactory.RegisterCommands(Assembly)"/> call prefers that
    /// assembly's source-generated <c>DiscoveredCommands</c> manifest and only falls back to a
    /// reflective scan when the manifest is absent. The manifest is therefore a per-assembly
    /// optimization that still covers built-in + application + extension commands; an app is
    /// fully trim-clean when every contributing assembly was built with JasperFx.SourceGenerator.
    /// The suppression below documents that the trim-unsafe fallback is only reached for
    /// assemblies that omit the generator.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Fallback assembly scan is only reached for assemblies without a source-generated DiscoveredCommands manifest. Consumers targeting trim/AOT wire JasperFx.SourceGenerator, which emits the manifest as ordinary code in each consuming assembly so RegisterCommands(Assembly) never reaches GetExportedTypes at runtime. Apps that omit the analyzer are by definition opting into the runtime-codegen path and accept the scan.")]
    internal static void ApplyFactoryDefaults(this CommandFactory factory, Assembly? applicationAssembly)
    {
        factory.RegisterCommands(typeof(RunCommand).GetTypeInfo().Assembly);

        if (applicationAssembly != null)
        {
            factory.RegisterCommands(applicationAssembly);
        }

        factory.RegisterCommandsFromExtensionAssemblies();
    }

    [RequiresUnreferencedCode("Builds a CommandExecutor that resolves commands reflectively.")]
    [RequiresDynamicCode("CommandExecutor.ExecuteAsync closes generic List<T> via MakeGenericType for enumerable arguments.")]
    private static Task<int> execute(IHostBuilder runtimeSource, Assembly? applicationAssembly, string[] args,
        string? optionsFile)
    {
        args = args.ApplyArgumentDefaults(optionsFile);

        var commandExecutor = buildExecutor(runtimeSource, applicationAssembly);
        return commandExecutor.ExecuteAsync(args);
    }

    [RequiresUnreferencedCode("Builds the default ActivatorCommandCreator-backed factory and walks command-instance properties via reflection.")]
    private static CommandExecutor buildExecutor(IHostBuilder source, Assembly? applicationAssembly)
    {
        if (JasperFxEnvironment.AutoStartHost && source is PreBuiltHostBuilder b)
        {
            b.Host.Start();
        }

        #region sample_using_extension_assemblies

        return CommandExecutor.For(factory =>
        {
            factory.ApplyFactoryDefaults(applicationAssembly);

            factory.ConfigureRun = commandRun =>
            {
                if (commandRun.Input is IHostBuilderInput i)
                {
                    factory.ApplyExtensions(source);
                    i.HostBuilder = source;
                }
                else if (GeneratedCommandRegistry.TryGetInjector(commandRun.Command.GetType(), out var inject))
                {
                    commandRun.Command = new HostWrapperCommand(commandRun.Command, source.Build, inject);
                }
                else
                {
                    var props = commandRun.Command.GetType().GetProperties()
                        .Where(x => x.HasAttribute<InjectServiceAttribute>())
                        .ToArray();

                    if (props.Any())
                    {
                        commandRun.Command = new HostWrapperCommand(commandRun.Command, source.Build, props);
                    }
                }
            };
        });

        #endregion
    }

    /// <summary>
    ///     Execute the extended JasperFx command line support for your configured IHost.
    ///     This method would be called within the Task&lt;int&gt; Program.Main(string[] args) method
    ///     of your AspNetCore application. This usage is appropriate for WebApplication bootstrapping.
    /// </summary>
    /// <param name="host">An already built IHost</param>
    /// <param name="args"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode(RunCommandsTrimMessage)]
    [RequiresDynamicCode(RunCommandsDynamicCodeMessage)]
    public static Task<int> RunJasperFxCommands(this IHost host, string[] args)
    {
        // Workaround for IISExpress / VS2019 erroneously putting crap arguments
        args = args.FilterLauncherArgs();

        if (args.Any(x => _aspnetCoreFlags.Any(flag => x.EqualsIgnoreCase(flag))))
        {
            Console.WriteLine("Detected reserved AspNetCore or .NET flags, so bypassing JasperFx command line and using IHost.RunAsync()");
            return host.RunAsync().ContinueWith(_ => 0);
        }
        
        return execute(new PreBuiltHostBuilder(host), Assembly.GetEntryAssembly(), args, null);
    }

    private static string[] _aspnetCoreFlags = ["--urls", "--http_ports", "--https_ports"];
}