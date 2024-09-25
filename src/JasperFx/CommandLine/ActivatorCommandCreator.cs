namespace JasperFx.CommandLine;

public class ActivatorCommandCreator : ICommandCreator
{
    public IOaktonCommand CreateCommand(Type commandType)
    {
        return (IOaktonCommand)Activator.CreateInstance(commandType);
    }

    public object CreateModel(Type modelType)
    {
        return Activator.CreateInstance(modelType);
    }
}