using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using JasperFx.CommandLine.Help;
using JasperFx.CommandLine.Parsing;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Core.TypeScanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace JasperFx.CommandLine;

public class CommandFactory : ICommandFactory
{
    private static readonly string[] _helpCommands = { "help", "?" };


    private static readonly Regex regex = new("(?<name>.+)Command", RegexOptions.Compiled);

    // Only used to disable the console writing on errors
    private static bool _hasAppliedExtensions;
    private readonly ICommandCreator _commandCreator;
    private readonly LightweightCache<string, Type> _commandTypes = new();

    private readonly IList<Type> _extensionTypes = new List<Type>();
    private string _appName = null!;

    private Type? _defaultCommand;

    /// <summary>
    ///     Perform some operation based on command inputs, before command construction
    /// </summary>
    public Action<string, object>? BeforeBuild = null;

    /// <summary>
    ///     Alter the input object or the command object just before executing the command
    /// </summary>
    public Action<CommandRun> ConfigureRun = run => { };

    public CommandFactory()
    {
        _commandCreator = new ActivatorCommandCreator();
    }

    public CommandFactory(ICommandCreator creator)
    {
        _commandCreator = creator;
    }

    /// <summary>
    ///     Optionally designates the default command type. Useful if your console app only has one command
    /// </summary>
    public Type? DefaultCommand
    {
        get => _defaultCommand ?? (_commandTypes.Count == 1 ? _commandTypes.Single() : null);
        set
        {
            _defaultCommand = value;
            if (value != null)
            {
                _commandTypes[CommandNameFor(value)] = value;
            }
        }
    }

    [RequiresUnreferencedCode("CommandFactory dispatches to commands and inputs via reflection; their public constructors and properties must survive trimming. AOT-publishing apps should consume commands through the source-generated manifest.")]
    [RequiresDynamicCode("Command input parsing closes generic List<T> via MakeGenericType for enumerable arguments / flags.")]
    public CommandRun BuildRun(string commandLine)
    {
        var args = StringTokenizer.Tokenize(commandLine);
        return BuildRun(args);
    }

    [RequiresUnreferencedCode("CommandFactory dispatches to commands and inputs via reflection; their public constructors and properties must survive trimming. AOT-publishing apps should consume commands through the source-generated manifest.")]
    [RequiresDynamicCode("Command input parsing closes generic List<T> via MakeGenericType for enumerable arguments / flags.")]
    public CommandRun BuildRun(IEnumerable<string> args)
    {
        if (!args.Any())
        {
            if (DefaultCommand == null)
            {
                return HelpRun(new Queue<string>());
            }
        }

        args = ArgPreprocessor.Process(args);

        var queue = new Queue<string>(args);

        if (queue.Count == 0 && DefaultCommand != null)
        {
            return buildRun(queue, CommandNameFor(DefaultCommand));
        }

        var firstArg = queue.Peek().ToLowerInvariant();

        if (_helpCommands.Contains(firstArg))
        {
            queue.Dequeue();

            return HelpRun(queue);
        }

        if (_commandTypes.Contains(firstArg))
        {
            queue.Dequeue();
            return buildRun(queue, firstArg);
        }

        if (DefaultCommand != null)
        {
            return buildRun(queue, CommandNameFor(DefaultCommand));
        }

        return InvalidCommandRun(firstArg);
    }

    /// <summary>
    ///     Add all the IJasperFxCommand classes in the given assembly to the command runner
    /// </summary>
    /// <param name="assembly"></param>
    [RequiresUnreferencedCode("Scans assembly.GetExportedTypes() for IJasperFxCommand. AOT-publishing apps should rely on the source-generated command manifest emitted by JasperFx.SourceGenerator.")]
    public void RegisterCommands(Assembly assembly)
    {
        // Prefer the source-generated command manifest for this assembly (trim/AOT clean,
        // no GetExportedTypes scan). Falls back to reflection per-assembly when the assembly
        // was not built with JasperFx.SourceGenerator.
        if (TryRegisterCommandsFromManifest(assembly))
        {
            return;
        }

        foreach (var type in assembly
                     .GetExportedTypes()
                     .Where(IsJasperFxCommandType))
            _commandTypes[CommandNameFor(type)] = type;

        if (assembly.TryGetAttribute<JasperFxAssemblyAttribute>(out var attribute))
        {
            if (attribute.ExtensionType != null)
            {
                _extensionTypes.Add(attribute.ExtensionType);
            }

        }
    }

    [RequiresUnreferencedCode("BuildAllCommands dispatches each registered command type through ICommandCreator.CreateCommand, which instantiates the type reflectively.")]
    public IEnumerable<IJasperFxCommand> BuildAllCommands()
    {
        return _commandTypes.Select(x => _commandCreator.CreateCommand(x));
    }

    [RequiresUnreferencedCode("Activator.CreateInstance(extensionType) requires public parameterless constructor of each extension to survive trimming.")]
    public void ApplyExtensions(IHostBuilder builder)
    {
        if (builder is PreBuiltHostBuilder)
        {
            return;
        }

        if (_extensionTypes.Any())
        {
            builder.ConfigureServices(ApplyExtensions);
        }
    }

    [RequiresUnreferencedCode("Activator.CreateInstance(extensionType) requires public parameterless constructor of each extension to survive trimming.")]
    public void ApplyExtensions(IServiceCollection services)
    {
        try
        {
            foreach (var extensionType in _extensionTypes)
            {
                var extension = Activator.CreateInstance(extensionType) as IServiceRegistrations;
                extension?.Configure(services);
            }

            _hasAppliedExtensions = true;
        }
        catch (Exception)
        {
            // Swallow the error
            if (_hasAppliedExtensions)
            {
                return;
            }

            AnsiConsole.MarkupLine(
                $"[red]Unable to apply JasperFx extensions. Try adding IHostBuilder.{nameof(CommandLineHostingExtensions.ApplyJasperFxExtensions)}(); to your bootstrapping code to apply JasperFx extension loading[/]");
        }
    }

    public IEnumerable<Type> AllCommandTypes()
    {
        return _commandTypes;
    }

    public CommandRun InvalidCommandRun(string commandName)
    {
        return new CommandRun
        {
            Command = new HelpCommand(),
            Input = new HelpInput
            {
                AppName = _appName,
                Name = commandName,
                CommandTypes = _commandTypes.ToArray(),
                InvalidCommandName = true
            }
        };
    }

    [RequiresUnreferencedCode("Resolves the command type reflectively via ICommandCreator and walks its UsageGraph (which reads MemberInfo via reflection).")]
    [RequiresDynamicCode("Enumerable argument / flag parsing closes generic List<T> via MakeGenericType.")]
    private CommandRun buildRun(Queue<string> queue, string commandName)
    {
        try
        {
            object? input = null;

            if (BeforeBuild != null)
            {
                input = tryBeforeBuild(queue, commandName);
            }

            var command = Build(commandName);

            input ??= command.Usages.BuildInput(queue, _commandCreator);
            var run = new CommandRun
            {
                Command = command,
                Input = input
            };

            ConfigureRun(run);

            return run;
        }
        catch (InvalidUsageException e)
        {
            AnsiConsole.MarkupLine("[red]Invalid usage[/]");

            if (e.Message.IsNotEmpty())
            {
                AnsiConsole.MarkupLine($"[yellow]{e.Message.EscapeMarkup()}[/]");
            }

            Console.WriteLine();
        }
        catch (Exception e)
        {
            AnsiConsole.MarkupLine("[red]Error parsing input[/]");
            AnsiConsole.WriteException(e);

            Console.WriteLine();
        }

        return HelpRun(commandName);
    }

    [RequiresUnreferencedCode("Builds a temporary command instance to pre-populate input. Inherits trim requirements from ActivatorCommandCreator.CreateCommand.")]
    [RequiresDynamicCode("UsageGraph input building closes generic List<T> for enumerable arguments.")]
    private object? tryBeforeBuild(Queue<string> queue, string commandName)
    {
        var commandType = _commandTypes[commandName];

        try
        {
            var defaultConstructorCommand = new ActivatorCommandCreator().CreateCommand(commandType);
            var input = defaultConstructorCommand.Usages.BuildInput(queue, _commandCreator);

            BeforeBuild?.Invoke(commandName, input);

            return input;
        }
        catch (MissingMethodException)
        {
            // Command has no default constructor - not possible to pre-configure from inputs.
            return null;
        }
    }

    /// <summary>
    ///     Add a single command type to the command runner
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [RequiresUnreferencedCode("Registers T for later reflective instantiation via ICommandCreator; T's public constructors and input-type properties must survive trimming.")]
    public void RegisterCommand<T>()
    {
        RegisterCommand(typeof(T));
    }

    /// <summary>
    ///     Add a single command type to the command runner
    /// </summary>
    [RequiresUnreferencedCode("Registers a command type for later reflective instantiation via ICommandCreator; its public constructors and input-type properties must survive trimming.")]
    public void RegisterCommand(Type type)
    {
        if (!IsJasperFxCommandType(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type),
                $"Type '{type.FullName}' does not inherit from either JasperFxCommannd or JasperFxAsyncCommand");
        }

        _commandTypes[CommandNameFor(type)] = type;
    }

    public static bool IsJasperFxCommandType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
    {
        if (!type.IsConcrete())
        {
            return false;
        }

        return type.Closes(typeof(JasperFxCommand<>)) || type.Closes(typeof(JasperFxAsyncCommand<>));
    }


    [RequiresUnreferencedCode("Resolves the command type reflectively via ICommandCreator. The type's public constructor must survive trimming.")]
    public IJasperFxCommand Build(string commandName)
    {
        return _commandCreator.CreateCommand(_commandTypes[commandName.ToLower()]);
    }


    [RequiresUnreferencedCode("Resolves the command and its UsageGraph reflectively via ICommandCreator. The command type's public constructor and input properties must survive trimming.")]
    [RequiresDynamicCode("UsageGraph input building closes generic List<T> for enumerable arguments.")]
    public CommandRun HelpRun(string commandName)
    {
        return HelpRun(new Queue<string>(new[] { commandName }));
    }

    [RequiresUnreferencedCode("Resolves the command and its UsageGraph reflectively via ICommandCreator. The command type's public constructor and input properties must survive trimming.")]
    [RequiresDynamicCode("UsageGraph input building closes generic List<T> for enumerable arguments.")]
    public virtual CommandRun HelpRun(Queue<string> queue)
    {
        var input = (HelpInput)new HelpCommand().Usages.BuildInput(queue, _commandCreator);
        input.CommandTypes = _commandTypes.ToArray();

        // Little hokey, but show the detailed help for the default command
        if (DefaultCommand != null && input.CommandTypes.Count() == 1)
        {
            input.Name = CommandNameFor(DefaultCommand);
        }


        if (input.Name.IsNotEmpty())
        {
            input.InvalidCommandName = true;
            input.Name = input.Name.ToLowerInvariant();

            if (_commandTypes.TryFind(input.Name, out var type))
            {
                input.InvalidCommandName = false;

                var cmd = _commandCreator.CreateCommand(type);

                input.Usage = cmd.Usages;
            }
        }

        return new CommandRun
        {
            Command = new HelpCommand(),
            Input = input
        };
    }

    public static string CommandNameFor(Type type)
    {
        var match = regex.Match(type.Name);
        var name = type.Name;
        if (match.Success)
        {
            name = match.Groups["name"].Value;
        }

        type.ForAttribute<DescriptionAttribute>(att => name = att.Name ?? name);

        return name.ToLower();
    }

    public static string DescriptionFor(Type type)
    {
        var description = type.FullName;
        type.ForAttribute<DescriptionAttribute>(att => description = att.Description);

        return description!;
    }

    public void SetAppName(string appName)
    {
        _appName = appName;
    }

    /// <summary>
    ///     Automatically discover any JasperFx commands in assemblies marked as
    ///     [assembly: JasperFxCommandAssembly]. Also
    /// </summary>
    /// <remarks>
    ///     Tries the source-generated <c>DiscoveredCommands</c> manifest first
    ///     (AOT/trim-clean path); falls back to <see cref="AssemblyFinder"/> +
    ///     <see cref="RegisterCommands"/> if no manifest is found. The trim/AOT
    ///     warnings are attached because the fallback path scans assemblies.
    /// </remarks>
    [RequiresUnreferencedCode("Falls back to AssemblyFinder + assembly.GetExportedTypes() scanning if no source-generated command manifest is present. AOT-publishing apps should emit the manifest via JasperFx.SourceGenerator.")]
    public void RegisterCommandsFromExtensionAssemblies()
    {
        // Each RegisterCommands(assembly) call below prefers that assembly's source-generated
        // manifest and only falls back to reflective scanning when the manifest is absent, so
        // the optimization is applied per-assembly rather than all-or-nothing.
        var assemblies = AssemblyFinder
            .FindAssemblies(a => a.HasAttribute<JasperFxAssemblyAttribute>() && !a.IsDynamic)
            .Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Where(a => a.HasAttribute<JasperFxAssemblyAttribute>() && !a.IsDynamic)
            .Distinct()
            .ToArray();

        foreach (var assembly in assemblies)
        {
            if (!_hasAppliedExtensions)
            {
                AnsiConsole.MarkupLine($"[gray]Searching '{assembly.FullName}' for commands[/]");
            }

            RegisterCommands(assembly);
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    ///     Attempt to register the commands of a single assembly from its source-generated
    ///     <c>JasperFx.Generated.DiscoveredCommands</c> manifest, avoiding a reflective
    ///     <see cref="Assembly.GetExportedTypes"/> scan. Returns true if the assembly carries a
    ///     manifest (so the caller should skip its reflective fallback), false otherwise.
    /// </summary>
    /// <remarks>
    ///     The lookup uses well-known string identifiers
    ///     (<c>JasperFx.Generated.DiscoveredCommands</c> + <c>CommandTypes</c>) that
    ///     the trimmer cannot statically prove are reachable. The
    ///     <see cref="UnconditionalSuppressMessage"/> attributes document that
    ///     consuming apps emit the manifest via the JasperFx.SourceGenerator
    ///     analyzer — when that source generator runs, the type and property are
    ///     produced as ordinary code in the consuming assembly and survive
    ///     trimming naturally. Apps that do not include the generator simply
    ///     fall through to <see cref="RegisterCommands(Assembly)"/>, which carries its own
    ///     <c>[RequiresUnreferencedCode]</c>.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "JasperFx.Generated.DiscoveredCommands is emitted by JasperFx.SourceGenerator into the consuming app as ordinary code; the lookup degrades safely to the reflective fallback if the generator is not enabled.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
        Justification = "Same as IL2026 — DiscoveredCommands.CommandTypes is generated source code in the consuming assembly.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
        Justification = "Types come from JasperFx.Generated.DiscoveredCommands.CommandTypes which is emitted by the source generator with full interface metadata preserved.")]
    internal bool TryRegisterCommandsFromManifest(Assembly assembly)
    {
        if (assembly.IsDynamic) return false;

        // The source generator emits this class in the JasperFx.Generated namespace of the
        // assembly it runs against.
        var manifestType = assembly.GetType("JasperFx.Generated.DiscoveredCommands");
        if (manifestType == null) return false;

        var prop = manifestType.GetProperty("CommandTypes",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (prop == null) return false;

        if (prop.GetValue(null) is not IEnumerable<Type> commandTypes) return false;

        foreach (var type in commandTypes)
        {
            if (IsJasperFxCommandType(type))
            {
                _commandTypes[CommandNameFor(type)] = type;
            }
        }

        // Mirror RegisterCommands: also register the assembly's extension type, if any.
        if (assembly.TryGetAttribute<JasperFxAssemblyAttribute>(out var attribute)
            && attribute.ExtensionType != null)
        {
            _extensionTypes.Add(attribute.ExtensionType);
        }

        // NOTE: deliberately no console output here. RegisterCommands(Assembly) calls this, and
        // that low-level API must stay side-effect free — writing to the shared AnsiConsole during
        // command registration perturbs callers that redirect Console.Out (e.g. test harnesses).
        return true;
    }
}