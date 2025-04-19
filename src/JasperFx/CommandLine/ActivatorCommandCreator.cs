namespace JasperFx.CommandLine;

public class ActivatorCommandCreator : ICommandCreator
{
    public IJasperFxCommand CreateCommand(Type commandType)
    {
        return (IJasperFxCommand)Activator.CreateInstance(commandType)!;
    }

    public object CreateModel(Type modelType)
    {
        return Activator.CreateInstance(modelType)!;
    }
}