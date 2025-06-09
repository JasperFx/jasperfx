using System.Diagnostics;
using JasperFx.Core.Reflection;
using Spectre.Console;

namespace JasperFx.CommandLine;

public class ActivatorCommandCreator : ICommandCreator
{
    public ActivatorCommandCreator()
    {
        Debug.WriteLine("What?");
    }

    public IJasperFxCommand CreateCommand(Type commandType)
    {
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

    public object CreateModel(Type modelType)
    {
        return Activator.CreateInstance(modelType)!;
    }
}