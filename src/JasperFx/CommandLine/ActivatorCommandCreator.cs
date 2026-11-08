using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using Spectre.Console;

namespace JasperFx.CommandLine;

public class ActivatorCommandCreator : ICommandCreator
{
    public ActivatorCommandCreator()
    {
        Debug.WriteLine("What?");
    }

    [RequiresUnreferencedCode("Activator.CreateInstance(Type) requires the public parameterless constructor of commandType to survive trimming.")]
    public IJasperFxCommand CreateCommand(Type commandType)
    {
        if (GeneratedCommandRegistry.TryCreate(commandType, out var generated))
        {
            return (IJasperFxCommand)generated!;
        }

        try
        {
            return (IJasperFxCommand)Activator.CreateInstance(commandType)!;
        }
        catch (MissingMethodException)
        {
            throw new InvalidOperationException(
                "JasperFx does not yet support constructor injection for commands, please use a default ctor and setter injection with properties decorated with [InjectService]. Command type: " +
                commandType.FullNameInCode());
        }
    }

    [RequiresUnreferencedCode("Activator.CreateInstance(Type) requires the public parameterless constructor of modelType to survive trimming.")]
    public object CreateModel(Type modelType)
    {
        if (GeneratedCommandRegistry.TryCreate(modelType, out var generated))
        {
            return generated!;
        }

        return Activator.CreateInstance(modelType)!;
    }
}