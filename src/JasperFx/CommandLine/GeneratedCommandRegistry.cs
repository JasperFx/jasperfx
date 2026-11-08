using System.Collections.Concurrent;
using System.Reflection;

namespace JasperFx.CommandLine;

/// <summary>
///     Commands, and the factories that build them and their input models, as registered by the
///     <c>[ModuleInitializer]</c> that JasperFx.SourceGenerator emits beside each assembly's
///     <c>JasperFx.Generated.DiscoveredCommands</c> manifest.
/// </summary>
/// <remarks>
///     This is what makes the command line usable from a Native AOT binary (jasperfx#1141). The
///     manifest alone lists command types with <c>typeof(...)</c>, which keeps each type in the image
///     but not its constructor, so <see cref="ActivatorCommandCreator"/> failed with a
///     <see cref="MissingMethodException"/>; and finding the manifest by name with
///     <see cref="Assembly.GetType(string)"/> needs reflection metadata ILC does not keep either.
///     The generated module initializer instead hands over the command types and <c>new T()</c>
///     delegates as ordinary code, which the trimmer sees and keeps. These members are called by
///     generated code and are not meant for application code.
/// </remarks>
public static class GeneratedCommandRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<object>> _factories = new();
    private static readonly ConcurrentDictionary<Assembly, List<Type>> _commands = new();
    private static readonly ConcurrentDictionary<Type, Action<object, IServiceProvider>> _injectors = new();

    /// <summary>
    ///     Records a command type the source generator discovered, its factory when the command has
    ///     an accessible parameterless constructor, and, when it has <see cref="InjectServiceAttribute"/>
    ///     properties, the delegate that sets them from the application's services.
    /// </summary>
    public static void RegisterCommand(Type commandType, Func<object>? factory,
        Action<object, IServiceProvider>? injector = null)
    {
        var commands = _commands.GetOrAdd(commandType.Assembly, _ => new List<Type>());
        lock (commands)
        {
            if (!commands.Contains(commandType))
            {
                commands.Add(commandType);
            }
        }

        if (factory != null)
        {
            _factories[commandType] = factory;
        }

        if (injector != null)
        {
            _injectors[commandType] = injector;
        }
    }

    /// <summary>
    ///     Records the factory for a command's input model.
    /// </summary>
    public static void RegisterInput(Type inputType, Func<object> factory)
    {
        _factories[inputType] = factory;
    }

    /// <summary>
    ///     The command types the generated module initializer of <paramref name="assembly"/>
    ///     registered, or null when that assembly registered none.
    /// </summary>
    public static IReadOnlyList<Type>? CommandTypesFor(Assembly assembly)
    {
        if (!_commands.TryGetValue(assembly, out var commands))
        {
            return null;
        }

        lock (commands)
        {
            return commands.ToArray();
        }
    }

    internal static bool TryGetInjector(Type commandType, out Action<object, IServiceProvider> injector)
    {
        return _injectors.TryGetValue(commandType, out injector!);
    }

    internal static bool TryCreate(Type type, out object? instance)
    {
        if (_factories.TryGetValue(type, out var factory))
        {
            instance = factory();
            return true;
        }

        instance = null;
        return false;
    }
}
